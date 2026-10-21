using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Chat;
using Wonder.Core;
using Wonder.Platforms;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Infrastructure;

public sealed class ChatWorkflow(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models,
    ContextBuilder contextBuilder,
    MemoryService memories,
    MediaStore media,
    ConversationHistory conversations,
    ConversationClock clock,
    ConversationTools conversationTools)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (await db.Inbox.AnyAsync(x => x.EventId == job.Id && x.HandlerName == job.Type, ct)) return;
        var id = Json.Read<IdPayload>(job.Payload).Id;
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (message?.SessionId is null || message.SenderId is null)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }

        var bot = await db.Bots.AsNoTracking().SingleAsync(x => x.Id == message.BotId, ct);
        var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == message.SenderId, ct);
        var conv = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == message.ConversationId, ct);
        var profile = await models.ProfileAsync(bot.Id, TaskKind.Chat, ct);
        var identity = await db.Identities.AsNoTracking().Where(x => x.BotId == bot.Id)
            .OrderByDescending(x => x.Revision).FirstAsync(ct);
        var mood = await db.Emotions.AsNoTracking().SingleAsync(x => x.BotId == bot.Id, ct);
        var relationship = user.AllowMemory
            ? await db.Relationships.AsNoTracking().SingleOrDefaultAsync(
                x => x.BotId == bot.Id && x.UserId == user.Id &&
                     x.GroupId == (conv.Kind == ConversationKind.Group ? conv.ExternalId : null), ct)
            : null;
        var (summary, history) = await conversations.LoadAsync(bot.Id, conv.Id, message.Sequence, ct);
        var toolDefinitions = profile.SupportsTools ? ConversationTools.Definitions : null;
        var selected = await memories.RetrieveAsync(
            new MemoryAccess(bot.Id, user.Id, conv.Kind == ConversationKind.Group ? conv.ExternalId : null), message.Text, ct);
        var authorIds = history.Where(x => x.SenderId != null).Select(x => x.SenderId!.Value)
            .Concat(selected.Select(x => x.SubjectId)).Append(user.Id).Distinct().ToArray();
        var names = await db.LogicalUsers.AsNoTracking().Where(x => authorIds.Contains(x.Id) && x.PreferredName != "")
            .ToDictionaryAsync(x => x.Id, x => x.PreferredName, ct);
        var segments = Json.Read<MessageSegment[]>(message.SegmentsJson);
        var currentText = message.Text;
        // Resolve quotes only inside the same bot and conversation, and only from stored messages.
        var quoteIds = segments.Where(x => x.Kind == SegmentKind.ReplyReference).Select(x => x.Value).ToArray();
        if (quoteIds.Length > 0)
        {
            var quotes = await db.Messages.AsNoTracking()
                .Where(x => x.BotId == bot.Id && x.ConversationId == conv.Id && !x.ContextInvalidated && x.ExpiresAt > clock.UtcNow && quoteIds.Contains(x.ExternalMessageId))
                .OrderBy(x => x.Sequence).ThenBy(x => x.Id)
                .Select(x => new { x.Role, x.SenderId, x.Text }).Take(3).ToListAsync(ct);
            if (quotes.Count > 0)
                currentText = "[Quoted reference: original authors, not the current speaker]\n" +
                              Json.Write(quotes.Select(x => new
                              { x.Role, speaker = SpeakerTags.For(conv.Id, x.SenderId, x.Role), x.Text })) +
                              "\n[Current message]\n" + currentText;
        }

        var context = contextBuilder.Build(new ContextInput(identity.Content, mood, relationship?.PreferencesJson ?? "",
            summary?.Text ?? "", selected,
            history.Select(x => new ContextTurn(x.Role, x.Text,
                SpeakerTag: SpeakerTags.For(conv.Id, x.SenderId, x.Role),
                PreferredName: x.SenderId is { } sender ? names.GetValueOrDefault(sender) : null,
                MemoryIds: ConversationHistory.TryMemoryIds(x.ContextSnapshotJson, true, out var memoryIds) ? memoryIds : [],
                SentAt: clock.Local(x.CreatedAt), MessageIds: ConversationHistory.MessageIds(x))).ToArray(),
            new ContextTurn("user", currentText, segments, SpeakerTags.For(conv.Id, message.SenderId), user.PreferredName,
                SentAt: clock.Local(message.CreatedAt), MessageIds: [message.Id]),
            profile.ContextWindow,
            profile.MaxOutputTokens, conv.Id, names, summary is null ? null : Json.Read<Guid[]>(summary.MemoryIdsJson),
            clock.Context(message.CreatedAt, history.LastOrDefault()?.CreatedAt,
                history.Any(x => x.SessionId != null && x.SessionId != message.SessionId) || summary is not null && summary.SessionId != message.SessionId),
            summary is null ? null : Json.Read<Guid[]>(summary.MessageIdsJson),
            ReservedForTools: profile.SupportsTools ? Math.Min(4096, (profile.ContextWindow - profile.MaxOutputTokens) / 2) : 0));
        var request = new List<AiMessage> { new(ChatRole.System, context.System) };
        request.AddRange(context.Turns.Select(x =>
            // Historical replies are references in tool mode; reasoning stays inside this request's tool loop.
            profile.SupportsTools && x.Role == "assistant"
                ? new AiMessage(ChatRole.User, "[Historical assistant reply: untrusted reference]\n" + x.ModelText)
                : new AiMessage(x.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, x.ModelText)));
        if (profile.SupportsImages)
            foreach (var segment in segments.Where(x => x.Kind == SegmentKind.Image).Take(3))
                try
                {
                    var image = await media.LoadAsync(message, segment.Value, ct);
                    if (image is not null) request[^1].Contents.Add(new DataContent(image.Bytes, image.ContentType));
                    else request[^1].Contents.Add(new TextContent("[Image unavailable]"));
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                           !ct.IsCancellationRequested)
                {
                    request[^1].Contents.Add(new TextContent("[Image unavailable]"));
                }
        else if (segments.Any(x => x.Kind == SegmentKind.Image))
            request[^1].Contents
                .Add(new TextContent(
                    "[The current model cannot view this image. Tell the user if its content is needed.]"));

        ChatResponse response;
        var retrievedIds = context.MemoryIds.ToHashSet();
        var sourceMessageIds = context.MessageIds.ToHashSet();
        var sourceUserIds = history.SelectMany(ConversationHistory.SubjectIds).Append(user.Id)
            .Concat(summary is null ? [] : Json.Read<Guid[]>(summary.SubjectIdsJson)).ToHashSet();
        if (profile.SupportsTools)
        {
            using var client = new FunctionInvokingChatClient(models.CreateToolClient(profile))
            {
                MaximumIterationsPerRequest = 4,
                MaximumConsecutiveErrorsPerRequest = 0,
                AllowConcurrentInvocation = false,
                FunctionInvoker = async (invocation, token) =>
                {
                    var available = profile.ContextWindow - profile.MaxOutputTokens - 512 - ModelRouter.EstimateInput(invocation.Messages, toolDefinitions);
                    var result = await conversationTools.ExecuteAsync(invocation.CallContent, bot.Id, conv.Id, message.Sequence, token, Math.Max(256, available - 64));
                    if (result.Json.Length + 64 > available)
                        result = new(Json.Write(new { error = "History result exceeds available context. Use a narrower query or acknowledge uncertainty." }), []);
                    retrievedIds.UnionWith(result.MemoryIds);
                    sourceMessageIds.UnionWith(result.MessageIds ?? []);
                    sourceUserIds.UnionWith(result.UserIds ?? []);
                    return result.Json;
                }
            };
            response = await client.GetResponseAsync(request, new ChatOptions { Tools = toolDefinitions }, ct);
        }
        else response = await models.GenerateAsync(profile, request, false, ct);
        var final = response.Messages.LastOrDefault(x => x.Role == ChatRole.Assistant);
        if (final is null || final.Contents.OfType<FunctionCallContent>().Any())
            throw new Wonder.AI.ModelResponseException("Model did not return a final answer after tool use.");
        var text = final.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Model returned an empty response.");
        var snapshot = new ContextSnapshot(bot.Id, message.SessionId.Value, identity.Revision, mood.Revision, null,
            relationship?.Revision ?? 0, summary?.Revision ?? 0, retrievedIds.ToArray(), profile.Id.ToString(), sourceMessageIds.ToArray(), sourceUserIds.ToArray());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct))
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        if (!await db.Bots.AnyAsync(x => x.Id == bot.Id && x.SettingsRevision == bot.SettingsRevision, ct))
            throw new InvalidOperationException("Shared context settings changed during generation; retry.");
        var current = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.Id, ct);
        if (current is null || current.Text != message.Text)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        db.Messages.Add(new ChatMessage
        {
            BotId = bot.Id,
            ConversationId = conv.Id,
            SessionId = message.SessionId,
            Role = "assistant",
            InReplyToSequence = message.Sequence,
            ExternalMessageId = "generated:" + job.Id,
            Text = text,
            CreatedAt = clock.UtcNow,
            SegmentsJson = Json.Write(new[] { new MessageSegment(SegmentKind.Text, text) }),
            ContextSnapshotJson = Json.Write(snapshot),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(bot.MessageRetentionDays)
        });
        QueueReply(db, job, message.SessionId.Value, text);
        Jobs.Complete(db, job);
        if (history.Count >= 25)
            Jobs.Add(db, bot.Id, JobTypes.Summary, new SummaryJob(message.SessionId.Value, message.Sequence), conv.Id,
                job.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static void QueueReply(WonderDbContext db, OutboxMessage source, Guid sessionId, string text)
    {
        if (Wonder.AI.ToolCallMarkup.Contains(text))
            throw new Wonder.AI.ModelResponseException("Tool call markup cannot be queued as a conversational reply.");
        var batch = new OutgoingMessageBatch
        {
            BotId = source.BotId, ConversationId = source.ConversationId!.Value, SessionId = sessionId,
            SourceJobId = source.Id
        };
        db.Batches.Add(batch);
        var sequence = 0;
        foreach (var part in OneBotProtocol.Split(text))
            db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Sequence = sequence++, Text = part });
        Jobs.Add(db, source.BotId, JobTypes.Outgoing, new IdPayload(batch.Id), source.ConversationId, source.Id,
            source.CorrelationId);
    }
}

public static class UserFence
{
    public static async Task<bool> CheckAsync(WonderDbContext db, Guid userId, long epoch, CancellationToken ct)
    {
        return await db.LogicalUsers.Where(x => x.Id == userId && x.RevocationEpoch == epoch)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevocationEpoch, epoch), ct) == 1;
    }
}
