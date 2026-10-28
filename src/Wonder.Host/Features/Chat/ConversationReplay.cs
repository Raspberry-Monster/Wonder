using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.Host.Features.Chat;

public sealed record ReplayRead(RestoredReplay? Replay, long? Version, string Fingerprint, string Baseline,
    ConversationReplayDependency[] Dependencies);

public sealed class ConversationReplay(IConfiguration configuration, ILogger<ConversationReplay> logger)
{
    public bool Enabled => configuration.GetValue("Chat:ContextReplayEnabled", true);

    public static string Baseline(ConversationSummary? summary) => Json.Hash(Json.Write(summary is null ? null : new
    {
        summary.Id, summary.Revision, summary.ProjectionRevision, summary.ProjectionBaseSummaryRevision,
        summary.ProjectionState, summary.ThroughSequence
    }));

    public static string Fingerprint(string system, ModelProfile model, ModelContextBudget budget, IList<AITool>? tools,
        LogicalUser user, long identityRevision) => Json.Hash(Json.Write(new
    {
        protocol = "openai-chat-v1", layout = 1, identityRevision, system,
        model.Id, model.Endpoint, model.Model, model.SupportsImages, model.SupportsJson, model.SupportsTools,
        model.ThinkingEnabled, model.ReasoningEffort, model.TokenizerId, resolvedTokenizer = budget.Tokenizer,
        model.MessageTokenOverhead, model.ImageTokenBudget, model.ContextWindow, model.MaxOutputTokens,
        budget.InputLimit, budget.ToolReserve,
        userId = user.Id, user.AllowMemory, user.AllowNotes, user.RevocationEpoch, user.NotesEpoch,
        tools = tools?.OfType<AIFunctionDeclaration>().Select(x => new { x.Name, x.Description, x.JsonSchema }).ToArray()
    }));

    public async Task<ReplayRead> LoadAsync(WonderDbContext db, Guid bot, Guid conversation, LogicalUser user,
        long beforeSequence, string fingerprint, ConversationSummary? summary, DateTimeOffset now, CancellationToken ct)
    {
        var baseline = Baseline(summary);
        if (!Enabled) return new(null, null, fingerprint, baseline, []);
        var row = await db.ConversationReplays.AsNoTracking().SingleOrDefaultAsync(x => x.ConversationId == conversation, ct);
        ReplayRead Miss(string reason)
        {
            logger.LogInformation("Chat replay: Conversation={Conversation}, Outcome=rebuild, Reason={Reason}", conversation, reason);
            return new(null, row?.Version, fingerprint, baseline, []);
        }
        if (row is null) return Miss("missing");
        // A queued older turn may observe a newer checkpoint. Never delete or read that future state.
        if (row.ThroughSequence >= beforeSequence) return Miss("future_boundary");
        string? invalid = row.FormatVersion != ReplayMessages.Version ? "format" :
            row.BotId != bot || row.UserId != user.Id ? "scope" : row.ExpiresAt <= now ? "expired" :
            row.Fingerprint != fingerprint ? "configuration" : row.SummaryBaseline != baseline ? "summary_baseline" : null;
        var dependencies = await db.ReplayDependencies.AsNoTracking().Where(x => x.ConversationId == conversation)
            .ToArrayAsync(ct);
        try
        {
            if (invalid is null)
            {
                var payload = Json.Read<ReplayPayload>(row.PayloadJson);
                ReplayMessages.ValidateGroups(payload);
                if (payload.Messages[0].Contents is not [{ Kind: "text" }] ||
                    !await ContentSources.ValidAsync(db, bot, payload.Sources, ct, user.Id, now) ||
                    !await DependenciesValidAsync(db, bot, user.Id, dependencies, now, ct))
                    invalid = "sources";
                else
                {
                    var final = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.FinalMessageId &&
                        x.BotId == bot && x.ConversationId == conversation && x.Role == MessageRole.Assistant &&
                        x.InReplyToSequence == row.ThroughSequence, ct);
                    if (final is null || !payload.MessageIds.Contains(final.Id) ||
                        !payload.Sources.Contains(ContentSources.Message(final))) invalid = "final_message";
                    else
                    {
                        var gaps = await ConversationHistory.Visible(db, bot, conversation, beforeSequence, now)
                            .Where(x => (x.InReplyToSequence ?? x.Sequence) > row.ThroughSequence)
                            .OrderBy(x => x.Sequence).ToArrayAsync(ct);
                        foreach (var gap in gaps)
                            if (await ContentSources.MessageValidAsync(db, gap, ct, user.Id, now))
                            {
                                invalid = "history_gap";
                                break;
                            }
                    }
                }
                if (invalid is null)
                {
                    var messages = new List<AiMessage>();
                    foreach (var dto in payload.Messages)
                        messages.Add(await ReplayMessages.DecodeAsync(dto,
                            reference => LoadImageAsync(db, bot, reference, now, ct)));
                    var groups = payload.Groups.Select(x => new ReplayGroup(x.TurnSequence, x.MessageIds,
                        messages.Skip(x.Start).Take(x.Count).ToArray(), x.TurnSequence is null ? x.Weight : 40,
                        x.HistoryCount)).ToArray();
                    var last = Array.FindLastIndex(groups, x => x.HistoryCount > 0);
                    if (last >= 0) groups[last] = groups[last] with { Weight = 90 };
                    logger.LogInformation("Chat replay: Conversation={Conversation}, Outcome=hit, Turns={Turns}",
                        conversation, groups.Count(x => x.TurnSequence is not null));
                    return new(new(groups, payload.Sources, payload.MemoryIds, payload.MessageIds, payload.UserIds),
                        row.Version, fingerprint, baseline, dependencies);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or
                                       InvalidOperationException or IOException or UnauthorizedAccessException or NullReferenceException)
        {
            invalid = "unrecoverable_payload";
        }
        // Purge obsolete plaintext together with the dependency rows (FK cascade).
        await db.ConversationReplays.Where(x => x.ConversationId == conversation && x.Version == row.Version)
            .ExecuteDeleteAsync(ct);
        row = null;
        return Miss(invalid ?? "invalid");
    }

    public static async Task<DataContent> LoadImageAsync(WonderDbContext db, Guid bot, ReplayMedia reference,
        DateTimeOffset now, CancellationToken ct)
    {
        var asset = await db.Media.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reference.AssetId &&
            x.BotId == bot && x.ExpiresAt > now, ct) ?? throw new IOException("Replay media missing.");
        var bytes = await File.ReadAllBytesAsync(asset.Path, ct);
        if (asset.ContentType != reference.MediaType || asset.ContentHash != reference.ContentHash ||
            ReplayMessages.ImageHash(bytes) != reference.ContentHash)
            throw new IOException("Replay media changed.");
        return new DataContent(bytes, asset.ContentType);
    }

    public static async Task<bool> DependenciesValidAsync(WonderDbContext db, Guid bot, Guid user,
        IEnumerable<ConversationReplayDependency> dependencies, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.Kind == "Note")
            {
                var note = await db.Notes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == dependency.ResourceId &&
                    x.BotId == bot && x.UserId == user && x.DeletedAt == null && !x.PrivacyDeleted && !x.SourceInvalidated &&
                    db.LogicalUsers.Any(u => u.Id == user && u.AllowNotes), ct);
                if (note is null || note.Version.ToString(CultureInfo.InvariantCulture) != dependency.Version ||
                    !await NoteSources.MemorySourcesValidAsync(db, note, ct)) return false;
            }
            else if (dependency.Kind == "Media")
            {
                var asset = await db.Media.AsNoTracking().SingleOrDefaultAsync(x => x.Id == dependency.ResourceId &&
                    x.BotId == bot && x.ExpiresAt > now, ct);
                if (asset is null) return false;
                try
                {
                    await LoadImageAsync(db, bot, new(asset.Id, asset.ContentType, dependency.Version), now, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }
        }
        return true;
    }

    // Called in the reply's final transaction, after source/user/lease fences.
    public async Task SaveAsync(WonderDbContext db, ReplayRead read, Core.ChatMessage current, Core.ChatMessage final,
        IReadOnlyList<AiMessage>? actual, AiMessage answer, BuiltContext context, IEnumerable<ContentSource> consumed,
        IEnumerable<Guid> memoryIds, IEnumerable<Guid> messageIds, IEnumerable<Guid> userIds, Guid chatJob,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!Enabled || actual is null) return;
        var roots = consumed.Append(ContentSources.Message(final)).Distinct().ToArray();
        try
        {
            var retained = actual.Append(answer).ToArray();
            var assets = await db.Media.AsNoTracking().Where(x => x.BotId == current.BotId && x.ExpiresAt > now &&
                messageIds.Contains(x.MessageId)).OrderBy(x => x.Id).ToArrayAsync(ct);
            ReplayMedia? Media(DataContent data)
            {
                var hash = ReplayMessages.ImageHash(data.Data);
                var asset = assets.FirstOrDefault(x => x.ContentHash == hash && x.ContentType == data.MediaType);
                return asset is null ? null : new(asset.Id, asset.ContentType, hash);
            }
            var encoded = retained.Select(x => ReplayMessages.Encode(x, Media)).ToArray();
            var groups = new List<ReplayGroupData>();
            var offset = 1;
            foreach (var group in context.ReplayGroups ?? [])
            {
                var included = group.Messages.Where(actual.Contains).ToArray();
                if (included.Length == 0) continue;
                if (included.Length != group.Messages.Length ||
                    !retained.Skip(offset).Take(included.Length).SequenceEqual(included))
                    throw new NotSupportedException("Partial replay group.");
                groups.Add(new(offset, included.Length, group.TurnSequence, group.MessageIds, group.Weight, group.HistoryCount));
                offset += included.Length;
            }
            groups.Add(new(offset, retained.Length - offset, current.Sequence, [current.Id, final.Id], 90, 2));
            var payload = new ReplayPayload(ReplayMessages.Version, encoded, groups.ToArray(), roots,
                memoryIds.Distinct().ToArray(), messageIds.Append(final.Id).Distinct().ToArray(), userIds.Distinct().ToArray());
            ReplayMessages.ValidateGroups(payload);
            var dependencies = await CollectAsync(db, current.ConversationId, roots, final, chatJob,
                encoded.SelectMany(x => x.Contents).Where(x => x.Media is not null).Select(x => x.Media!), ct);
            var expiry = final.ExpiresAt;
            var sourceMessages = dependencies.Where(x => x.Kind == "Message").Select(x => x.ResourceId).ToArray();
            var messageExpiry = await db.Messages.Where(x => sourceMessages.Contains(x.Id)).MinAsync(x => (DateTimeOffset?)x.ExpiresAt, ct);
            if (messageExpiry < expiry) expiry = messageExpiry.Value;
            foreach (var asset in assets.Where(x => dependencies.Any(d => d.Kind == "Media" && d.ResourceId == x.Id)))
                if (asset.ExpiresAt < expiry) expiry = asset.ExpiresAt;
            var summaryIds = dependencies.Where(x => x.Kind == "SummaryProjection").Select(x => x.ResourceId).ToArray();
            var projectionExpiry = await db.Summaries.Where(x => summaryIds.Contains(x.Id)).MinAsync(x => x.ProjectionExpiresAt, ct);
            if (projectionExpiry < expiry) expiry = projectionExpiry.Value;
            if (expiry <= now) return;

            // Serialize competing final commits even when no checkpoint existed at admission.
            await db.Conversations.Where(x => x.Id == current.ConversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExternalId, x => x.ExternalId), ct);
            var existing = await db.ConversationReplays.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ConversationId == current.ConversationId, ct);
            if (existing?.Version != read.Version || existing?.ThroughSequence >= current.Sequence ||
                await db.Messages.AnyAsync(x => x.ConversationId == current.ConversationId &&
                    x.Role == MessageRole.Assistant && x.InReplyToSequence > current.Sequence, ct))
            {
                logger.LogInformation("Chat replay: Conversation={Conversation}, Outcome=skip, Reason=concurrent_state", current.ConversationId);
                return;
            }
            var json = Json.Write(payload);
            if (existing is null)
                db.ConversationReplays.Add(new ConversationReplayState
                {
                    ConversationId = current.ConversationId, BotId = current.BotId, UserId = current.SenderId!.Value,
                    ThroughSequence = current.Sequence, FinalMessageId = final.Id, Fingerprint = read.Fingerprint,
                    SummaryBaseline = read.Baseline, PayloadJson = json, UpdatedAt = now, ExpiresAt = expiry
                });
            else
            {
                var changed = await db.ConversationReplays.Where(x => x.ConversationId == current.ConversationId &&
                    x.Version == read.Version && x.ThroughSequence < current.Sequence).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.ThroughSequence, current.Sequence)
                    .SetProperty(x => x.FinalMessageId, final.Id).SetProperty(x => x.PayloadJson, json)
                    .SetProperty(x => x.Fingerprint, read.Fingerprint).SetProperty(x => x.SummaryBaseline, read.Baseline)
                    .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.ExpiresAt, expiry), ct);
                if (changed == 0) return;
                await db.ReplayDependencies.Where(x => x.ConversationId == current.ConversationId).ExecuteDeleteAsync(ct);
            }
            db.ReplayDependencies.AddRange(dependencies);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            logger.LogInformation("Chat replay: Conversation={Conversation}, Outcome=skip, Reason=unsupported_content", current.ConversationId);
        }
    }

    private static async Task<ConversationReplayDependency[]> CollectAsync(WonderDbContext db, Guid conversation,
        ContentSource[] roots, Core.ChatMessage final, Guid chatJob, IEnumerable<ReplayMedia> media, CancellationToken ct)
    {
        var seen = new HashSet<ContentSource>();
        var notes = new HashSet<(Guid, long)>();
        async Task Visit(ContentSource source, int depth)
        {
            if (depth > 16 || seen.Count > 512) throw new NotSupportedException("Replay dependency limit.");
            if (!seen.Add(source)) return;
            if (source.Kind == "Message")
            {
                var message = source.Id == final.Id ? final : await db.Messages.AsNoTracking().SingleAsync(x => x.Id == source.Id, ct);
                if (message.ContextSnapshotJson is { } snapshot)
                    foreach (var child in Json.Read<ContextSnapshot>(snapshot).Sources ?? []) await Visit(child, depth + 1);
                foreach (var note in await db.NoteDependencies.AsNoTracking().Where(x =>
                             x.TargetKind == NoteDependencyTargetKind.Message && x.TargetId == source.Id).ToArrayAsync(ct))
                    notes.Add((note.NoteId, note.Version));
            }
            else if (source.Kind is "Summary" or "SummaryProjection")
            {
                var summary = await db.Summaries.AsNoTracking().SingleAsync(x => x.Id == source.Id, ct);
                var children = source.Kind == "SummaryProjection" ? Json.Read<SafeSummaryProjection>(summary.SafeProjectionJson!).Sources :
                    Json.Read<ContentSource[]>(summary.SourcesJson!);
                foreach (var child in children) await Visit(child, depth + 1);
                if (source.Kind == "Summary")
                    foreach (var note in await db.NoteDependencies.AsNoTracking().Where(x =>
                                 x.TargetKind == NoteDependencyTargetKind.Summary && x.TargetId == source.Id).ToArrayAsync(ct))
                        notes.Add((note.NoteId, note.Version));
            }
        }
        foreach (var root in roots) await Visit(root, 0);
        foreach (var note in await db.NoteDependencies.AsNoTracking().Where(x =>
                     x.TargetKind == NoteDependencyTargetKind.Chat && x.TargetId == chatJob).ToArrayAsync(ct))
            notes.Add((note.NoteId, note.Version));
        return seen.Select(x => new ConversationReplayDependency { ConversationId = conversation, Kind = x.Kind,
                ResourceId = x.Id, Version = x.Version })
            .Concat(notes.Select(x => new ConversationReplayDependency { ConversationId = conversation, Kind = "Note",
                ResourceId = x.Item1, Version = x.Item2.ToString(CultureInfo.InvariantCulture) }))
            .Concat(media.Select(x => new ConversationReplayDependency { ConversationId = conversation, Kind = "Media",
                ResourceId = x.AssetId, Version = x.ContentHash })).DistinctBy(x => (x.Kind, x.ResourceId, x.Version)).ToArray();
    }

    public static Task<int> PurgeSourcesAsync(WonderDbContext db, string kind, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        return db.ConversationReplays.Where(x => db.ReplayDependencies.Any(d =>
            d.ConversationId == x.ConversationId && d.Kind == kind && ids.Contains(d.ResourceId))).ExecuteDeleteAsync(ct);
    }
}
