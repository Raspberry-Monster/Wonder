using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Host.Features.Chat;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using Wonder.Platforms;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed class ChatWorkflow(
    IDbContextFactory<WonderDbContext> factory,
    ModelRouter models,
    ContextBuilder contextBuilder,
    MemoryService memories,
    MediaStore media,
    ConversationHistory conversations,
    ConversationClock clock,
    ConversationTools conversationTools,
    InteractionBoundary interaction)
{
    public async Task RunAsync(Guid jobId, CancellationToken ct, Guid? expectedLeaseId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.Outbox.SingleAsync(x => x.Id == jobId, ct);
        Jobs.RequireLease(job, expectedLeaseId);
        if (job.Status == JobStatus.Cancelled) return;
        if (await db.Inbox.AnyAsync(x => x.EventId == job.Id && x.HandlerName == job.Type, ct)) return;
        if (await db.ChatDispositions.AnyAsync(x => x.ChatJobId == job.Id, ct))
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }
        var id = Json.Read<IdPayload>(job.Payload).Id;
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (message?.SessionId is null || message.SenderId is null || message.ContextInvalidated)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            return;
        }
        var force = await db.ForceReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.ChatJobId == job.Id, ct);
        if (force?.CompensationStatus == ForceCompensationOutcome.Refunded)
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }

        await using (var resetSources = await db.Database.BeginTransactionAsync(ct))
        {
            var leaseNow = await InteractionBoundary.DatabaseNowAsync(db, clock.UtcNow, ct);
            if (expectedLeaseId is { } lease && await db.Outbox.Where(x => x.Id == job.Id &&
                    x.Status == JobStatus.Running && x.LeaseId == lease && x.LeaseUntil > leaseNow)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, lease), ct) != 1)
                throw new DbUpdateConcurrencyException("Chat lease expired before source recovery.");
            await db.NoteDependencies.Where(x => x.TargetKind == NoteDependencyTargetKind.Chat && x.TargetId == job.Id).ExecuteDeleteAsync(ct);
            await resetSources.CommitAsync(ct);
        }
        var bot = await db.Bots.AsNoTracking().SingleAsync(x => x.Id == message.BotId, ct);
        var user = await db.LogicalUsers.AsNoTracking().SingleAsync(x => x.Id == message.SenderId, ct);
        var conv = await db.Conversations.AsNoTracking().SingleAsync(x => x.Id == message.ConversationId, ct);
        var profile = await models.ProfileAsync(bot.Id, TaskKind.Chat, ct);
        var identity = await db.Identities.AsNoTracking().Where(x => x.BotId == bot.Id)
            .OrderByDescending(x => x.Revision).FirstAsync(ct);
        var mood = await db.Emotions.AsNoTracking().SingleAsync(x => x.BotId == bot.Id, ct);
        var basicGroup = conv.Kind == ConversationKind.Group;
        var relationship = user.AllowMemory && !basicGroup
            ? await db.Relationships.AsNoTracking().SingleOrDefaultAsync(
                x => x.BotId == bot.Id && x.UserId == user.Id, ct)
            : null;
        var (summary, history) = basicGroup ? (null, (IReadOnlyList<ChatMessage>)[]) :
            await conversations.LoadAsync(bot.Id, conv.Id, message.Sequence, ct);
        var canWrite = !basicGroup && (interaction.Enabled || interaction.ReflectionEnabled || interaction.NotesEnabled) && expectedLeaseId is not null &&
                       await db.Users.AnyAsync(x => x.LogicalUserId == user.Id, ct);
        IList<AITool>? toolDefinitions = profile.SupportsTools
            ? (basicGroup ? ConversationTools.Definitions.Take(1) : ConversationTools.Definitions)
                .Concat(canWrite && interaction.Enabled ? InteractionBoundary.Definitions : [])
                .Concat(canWrite && interaction.ReflectionEnabled ? InteractionBoundary.ReflectionDefinitions : []).ToList() : null;
        if (canWrite && profile.SupportsTools && interaction.NotesEnabled && user.AllowNotes)
            toolDefinitions = toolDefinitions!.Concat(InteractionBoundary.NotesDefinitions).ToList();
        var selected = basicGroup ? new List<Core.Memory>() : await memories.RetrieveAsync(
            new MemoryAccess(bot.Id, user.Id),
            message.Text, ct);
        var authorIds = history.Where(x => x.SenderId != null).Select(x => x.SenderId!.Value)
            .Concat(selected.Select(x => x.SubjectId)).Append(user.Id).Distinct().ToArray();
        var names = await db.LogicalUsers.AsNoTracking().Where(x => authorIds.Contains(x.Id) && x.PreferredName != "")
            .ToDictionaryAsync(x => x.Id, x => x.PreferredName, ct);
        var segments = Json.Read<MessageSegment[]>(message.SegmentsJson);
        var currentText = message.Text;
        if (force is not null)
            currentText = $"[Server: authorized Force input; remaining at admission={force.RemainingAfter}. " +
                          "Replying does not unmute. You may explicitly choose interaction_no_reply.]\n" + currentText;
        // Resolve quotes only inside the same bot and conversation, and only from stored messages.
        var quoteIds = segments.Where(x => x.Kind == SegmentKind.ReplyReference).Select(x => x.Value).ToArray();
        var quotedSources = new List<ContentSource>();
        if (quoteIds.Length > 0 && !basicGroup)
        {
            var quotes = await db.Messages.AsNoTracking()
                .Where(x => x.BotId == bot.Id && x.ConversationId == conv.Id && !x.ContextInvalidated &&
                            x.ExpiresAt > clock.UtcNow && quoteIds.Contains(x.ExternalMessageId))
                .OrderBy(x => x.Sequence).ThenBy(x => x.Id).Take(3).ToListAsync(ct);
            var safeQuotes = new List<ChatMessage>();
            foreach (var quote in quotes)
                if (await ContentSources.MessageValidAsync(db, quote, ct, user.Id, clock.UtcNow)) safeQuotes.Add(quote);
            quotes = safeQuotes;
            quotedSources.AddRange(quotes.Select(ContentSources.Message));
            if (quotes.Count > 0)
                currentText = "[Quoted reference: original authors, not the current speaker]\n" +
                              Json.ForPrompt(quotes.Select(x => new
                                  { x.Role, speaker = SpeakerTags.For(conv.Id, x.SenderId, x.Role), x.Text })) +
                              "\n[Current message]\n" + currentText;
        }

        var relationshipProjection = relationship is null ? ((string Text, ContentSource Source)?)null :
            await RelationshipClaims.ProjectionAsync(db, relationship, ct);
        var context = contextBuilder.Build(new ContextInput(identity.Content, mood, relationshipProjection?.Text ?? "",
            summary is null ? "" : ConversationHistory.TextForModel(summary), selected,
            history.Select(x => new ContextTurn(x.Role, x.Text,
                SpeakerTag: SpeakerTags.For(conv.Id, x.SenderId, x.Role),
                PreferredName: x.SenderId is { } sender ? names.GetValueOrDefault(sender) : null,
                MemoryIds: ConversationHistory.TryMemoryIds(x.ContextSnapshotJson, true, out var memoryIds)
                    ? memoryIds
                    : [],
                SentAt: clock.Local(x.CreatedAt), MessageIds: ConversationHistory.MessageIds(x),
                TurnSequence: x.InReplyToSequence ?? x.Sequence, Sources: [ContentSources.Message(x)])).ToArray(),
            new ContextTurn(MessageRole.User, currentText, segments, SpeakerTags.For(conv.Id, message.SenderId),
                user.PreferredName,
                SentAt: clock.Local(message.CreatedAt), MessageIds: [message.Id, .. quotedSources.Select(x => x.Id)],
                Sources: [ContentSources.Message(message), .. quotedSources]),
            models.Context(profile), conv.Id, names, summary is null ? null : ConversationHistory.MemoryIdsForModel(summary),
            clock.Context(message.CreatedAt, history.LastOrDefault()?.CreatedAt,
                history.Any(x => x.SessionId != null && x.SessionId != message.SessionId) ||
                (summary is not null && summary.SessionId != message.SessionId)),
            summary is null ? null : ConversationHistory.MessageIdsForModel(summary),
            toolDefinitions, profile.SupportsImages, summary is null ? null : ContentSources.ProjectionReady(summary)
                ? ContentSources.Projection(summary) : ContentSources.Summary(summary), relationshipProjection?.Source));
        var request = context.Messages;
        foreach (var placeholder in request[^1].Contents.OfType<UriContent>().ToArray())
            request[^1].Contents.Remove(placeholder);
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
        var consumedSources = (context.Sources ?? []).ToHashSet();
        var sourceUserIds = history.SelectMany(ConversationHistory.SubjectIds).Append(user.Id)
            .Concat(summary is null ? [] : Json.Read<Guid[]>(summary.SubjectIdsJson)).ToHashSet();
        await NoteSources.InheritAsync(db, NoteDependencyTargetKind.Chat, job.Id, sourceMessageIds.ToArray(),
            summary is not null && !ContentSources.ProjectionReady(summary) ? summary.Id : null, ct);
        await db.SaveChangesAsync(ct);
        async Task ValidateSources(CancellationToken token)
        {
            if (!await ContentSources.ValidAsync(db, bot.Id, consumedSources, token, user.Id, clock.UtcNow))
                throw new DbUpdateConcurrencyException("Consumed content changed; rebuild the conversation.");
            if (!await NoteSources.ValidAsync(db, NoteDependencyTargetKind.Chat, job.Id, token))
                throw new DbUpdateConcurrencyException("Note source changed; rebuild the conversation.");
        }
        await ValidateSources(ct);
        // Mark processing immediately before handing content to the external model, never refund uncertain calls.
        models.Context(profile).Require(request, toolDefinitions);
        if (force is not null)
            await db.ForceReceipts.Where(x => x.Id == force.Id && x.ProcessingStartedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessingStartedAt, clock.UtcNow), ct);
        var ordinal = 0;
        var terminated = false;
        if (profile.SupportsTools)
        {
            var toolBudget = new ToolContextBudget(models.Context(profile), toolDefinitions!, context.OptionalGroups);
            using var client = new FunctionInvokingChatClient(models.CreateToolClient(profile, toolBudget.Prepare, ValidateSources))
            {
                MaximumIterationsPerRequest = 4,
                MaximumConsecutiveErrorsPerRequest = 0,
                AllowConcurrentInvocation = false,
                FunctionInvoker = async (invocation, token) =>
                {
                    var fits = toolBudget.ResultFits(invocation.Messages, invocation.CallContent.CallId);
                    if (terminated) throw new InvalidOperationException("Turn already terminated.");
                    ConversationToolResult result;
                    if (EnumWire.TryParse<AgentAction>(invocation.CallContent.Name, out _))
                    {
                        var json = !canWrite ? "{\"error\":\"UnavailableTool\"}" :
                            await interaction.ExecuteAsync(new AgentExecution(job.Id, expectedLeaseId!.Value,
                                bot.Id, conv.Id, user.Id, message.Id, user.RevocationEpoch, bot.SettingsRevision, user.NotesEpoch,
                                retrievedIds.ToArray(), sourceMessageIds.ToArray()),
                                ordinal++, invocation.CallContent, token);
                        result = new ConversationToolResult(json, []);
                        if (await db.ChatDispositions.AnyAsync(x => x.ChatJobId == job.Id, token))
                        {
                            terminated = true;
                            invocation.Terminate = true;
                        }
                    }
                    else result = await conversationTools.ExecuteAsync(invocation.CallContent, bot.Id, conv.Id,
                        message.Sequence, token, fits);
                    if (!fits(result.Json)) result = new ConversationToolResult("{\"error\":\"context_full\"}", []);
                    if (!fits(result.Json))
                        throw new ContextLimitException(
                            "The required tool conversation cannot fit even a minimal tool result.");
                    toolBudget.Remember(invocation.CallContent.CallId, result.Json);
                    retrievedIds.UnionWith(result.MemoryIds);
                    sourceMessageIds.UnionWith(result.MessageIds ?? []);
                    sourceUserIds.UnionWith(result.UserIds ?? []);
                    consumedSources.UnionWith(result.Sources ?? []);
                    await ValidateSources(token);
                    return result.Json;
                }
            };
            try { response = await client.GetResponseAsync(request, new ChatOptions { Tools = toolDefinitions }, ct); }
            catch
            {
                if (!await db.ChatDispositions.AnyAsync(x => x.ChatJobId == job.Id, CancellationToken.None)) throw;
                Jobs.Complete(db, job);
                await Jobs.SaveAsync(db, job, expectedLeaseId, CancellationToken.None);
                return;
            }
        }
        else
        {
            response = await models.GenerateAsync(profile, request, false, ct);
        }

        if (terminated || await db.ChatDispositions.AnyAsync(x => x.ChatJobId == job.Id, ct))
        {
            Jobs.Complete(db, job);
            await Jobs.SaveAsync(db, job, expectedLeaseId, ct);
            return;
        }
        var final = response.Messages.LastOrDefault(x => x.Role == ChatRole.Assistant);
        if (final is null || final.Contents.OfType<FunctionCallContent>().Any())
            throw new ModelResponseException("Model did not return a final answer after tool use.");
        var text = final.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("Model returned an empty response.");
        var snapshot = new ContextSnapshot(bot.Id, message.SessionId.Value, identity.Revision, mood.Revision,
            relationship?.Revision ?? 0, summary?.Revision ?? 0, retrievedIds.ToArray(), profile.Id.ToString(),
            sourceMessageIds.ToArray(), sourceUserIds.ToArray(), consumedSources.ToArray());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await UserFence.CheckAsync(db, user.Id, user.RevocationEpoch, ct))
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }
        await ValidateSources(ct);

        if (!await db.Bots.AnyAsync(x => x.Id == bot.Id && x.SettingsRevision == bot.SettingsRevision, ct))
            throw new InvalidOperationException("Bot settings changed during generation; retry.");
        var current = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == message.Id, ct);
        if (current is null || current.ContextInvalidated || current.Text != message.Text)
        {
            Jobs.Complete(db, job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var generated = new ChatMessage
        {
            BotId = bot.Id,
            ConversationId = conv.Id,
            SessionId = message.SessionId,
            Role = MessageRole.Assistant,
            InReplyToSequence = message.Sequence,
            ExternalMessageId = "generated:" + job.Id,
            Text = text,
            CreatedAt = clock.UtcNow,
            SegmentsJson = Json.Write(new[] { new MessageSegment(SegmentKind.Text, text) }),
            ContextSnapshotJson = Json.Write(snapshot),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(bot.MessageRetentionDays)
        };
        db.Messages.Add(generated);
        await NoteSources.CopyChatAsync(db, job.Id, generated.Id, ct);
        QueueReply(db, job, message.SessionId.Value, text, snapshot);
        Jobs.Complete(db, job);
        if (!basicGroup && context.NeedsSummary && !await db.Outbox.AnyAsync(x => x.BotId == bot.Id && x.ConversationId == conv.Id &&
                                                                   x.Type == JobKind.Summary &&
                                                                   x.Status == JobStatus.Pending, ct))
            Jobs.Add(db, bot.Id, JobKind.Summary, new SummaryJob(message.SessionId.Value, message.Sequence), conv.Id,
                job.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static void QueueReply(WonderDbContext db, OutboxMessage source, Guid sessionId, string text,
        ContextSnapshot? provenance = null, bool systemNotice = false)
    {
        if (ToolCallMarkup.Contains(text))
            throw new ModelResponseException("Tool call markup cannot be queued as a conversational reply.");
        var batch = new OutgoingMessageBatch
        {
            BotId = source.BotId, ConversationId = source.ConversationId!.Value, SessionId = sessionId,
            SourceJobId = source.Id, SourceSnapshotJson = provenance is null ? null : Json.Write(provenance), IsSystemNotice = systemNotice
        };
        db.Batches.Add(batch);
        var sequence = 0;
        foreach (var part in OneBotProtocol.Split(text))
            db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Sequence = sequence++, Text = part });
        Jobs.Add(db, source.BotId, JobKind.Outgoing, new IdPayload(batch.Id), source.ConversationId, source.Id,
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
