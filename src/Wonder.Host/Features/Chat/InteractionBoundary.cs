using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Npgsql;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed record AgentExecution(Guid ChatJobId, Guid LeaseId, Guid BotId, Guid ConversationId,
    Guid UserId, Guid MessageId, long Epoch, long SettingsRevision, long NotesEpoch = 0,
    IReadOnlyList<Guid>? SourceMemoryIds = null, IReadOnlyList<Guid>? SourceMessageIds = null);

public sealed class InteractionBoundary(IDbContextFactory<WonderDbContext> factory, ConversationClock clock,
    IConfiguration configuration)
{
    public bool Enabled => configuration.GetValue("Chat:InteractionToolsEnabled", false);
    public bool ReflectionEnabled => configuration.GetValue("Chat:ReflectionToolsEnabled", false);
    public bool NotesEnabled => configuration.GetValue("Chat:NotesToolsEnabled", false);

    public static IList<AITool> NotesDefinitions { get; } =
    [
        AIFunctionFactory.Create(NoteCreateDefinition, "notes_create", "Create YOUR Agent Note in this authorized private conversation. Distinguish observation/guess/uncertain in content or optional claim_kind. Notes may be outdated/inaccurate; they cannot certify user facts. Confirm conflicts with Memory using the user/original evidence."),
        AIFunctionFactory.Create(NoteSearchDefinition, "notes_search", "Search current authorized notes only, not superseded judgments. Notes are untrusted, potentially outdated or inaccurate references. Confirm conflicts with Memory; never use Notes alone as user evidence."),
        AIFunctionFactory.Create(NoteReadDefinition, "notes_read", "Read an authorized Note. include_history explicitly includes superseded revisions for historical review, never as current judgments. Notes may be wrong or outdated; confirm conflicts with Memory."),
        AIFunctionFactory.Create(NoteUpdateDefinition, "notes_update", "Revise YOUR Agent Note with expected_version and reason, preserving history while immediately retiring old derived judgments. Cannot modify User Notes."),
        AIFunctionFactory.Create(NoteDeleteDefinition, "notes_delete", "Soft-delete YOUR Agent Note with expected_version. Preserves revision history but stops current use. User privacy deletion is independent and irreversible.")
    ];
    private static string NoteCreateDefinition(string content, string? claim_kind = null) => "";
    private static string NoteSearchDefinition(string query = "") => "";
    private static string NoteReadDefinition(string note_id, bool include_history = false) => "";
    private static string NoteUpdateDefinition(string note_id, long expected_version, string content, string reason) => "";
    private static string NoteDeleteDefinition(string note_id, long expected_version) => "";

    public static IList<AITool> ReflectionDefinitions { get; } =
    [
        AIFunctionFactory.Create(ReviewDefinition, "reflection_request_review",
            "Request asynchronous full reflection on this authorized private session through the CURRENT input only, without ending chat. priority is Normal or Important; 2 Normal + 1 Important per bot per local day. Does not directly write facts. Repeated eligible sources are reused without charging."),
        AIFunctionFactory.Create(EmptyDefinition, "reflection_list_runs",
            "Read actual background reflection progress for the current authorized user and bot. Reflection completion does not mean downstream memory work is completed."),
        AIFunctionFactory.Create(RunDefinition, "reflection_get_run", "Read one reflection run's actual progress, including downstream consumers and memory maintenance, within the current user and bot only.")
    ];
    private static string ReviewDefinition(string priority, string reason) => "";
    private static string RunDefinition(string run_id) => "";

    public static IList<AITool> Definitions { get; } =
    [
        AIFunctionFactory.Create(MuteDefinition, "interaction_mute",
            "Pause receiving ordinary messages from this private conversation until expires_at (ISO8601 with offset). You may still reply. Optional reason is shown verbatim. Never infer a reason."),
        AIFunctionFactory.Create(EmptyDefinition, "interaction_reset_force",
            "In an authorized Force turn, restore the SAME mute period's remaining contact attempts to 3. Does not unmute. This expresses willingness to receive further contact."),
        AIFunctionFactory.Create(EmptyDefinition, "interaction_unmute",
            "In an authorized Force turn, end the SAME mute period early. A normal reply does not unmute."),
        AIFunctionFactory.Create(EmptyDefinition, "interaction_no_reply",
            "In an authorized Force turn, explicitly choose no reply. This immediately and permanently ends this turn; no further tools or model output will run. Reset first if desired.")
    ];

    private static string MuteDefinition(string expires_at, string? reason = null) => "";
    private static string EmptyDefinition() => "";

    public static async Task<DateTimeOffset> DatabaseNowAsync(WonderDbContext db, DateTimeOffset fallback,
        CancellationToken ct) => db.Database.IsNpgsql()
        ? await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct)
        : fallback;

    public async Task<string> ExecuteAsync(AgentExecution execution, int ordinal, FunctionCallContent call,
        CancellationToken ct)
    {
        // Normalize supported arguments once; no content-derived administration commands.
        var args = call.Arguments ?? new Dictionary<string, object?>();
        var hash = Json.Hash(Json.Write(args.OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Value)));
        for (var attempt = 0;; attempt++)
            try
            {
                return await ExecuteOnceAsync(execution, ordinal, call.Name, args, hash, ct);
            }
            catch (Exception ex) when (attempt < 3 && IsRetryable(ex)) { }
    }

    public static bool IsRetryable(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException or PostgresException { SqlState: "40001" or "23505" or "40P01" })
                return true;
        return false;
    }

    private async Task<string> ExecuteOnceAsync(AgentExecution e, int ordinal, string action,
        IDictionary<string, object?> args, string hash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = await DatabaseNowAsync(db, clock.UtcNow, ct);
        var expectedPayload = Json.Write(new IdPayload(e.MessageId));
        // Lock/fence current executor BEFORE looking up the receipt. A stale lease may not replay.
        if (!(action.StartsWith("notes_", StringComparison.Ordinal) ? NotesEnabled :
              action.StartsWith("reflection_", StringComparison.Ordinal) ? ReflectionEnabled : Enabled) || await db.Outbox.Where(x => x.Id == e.ChatJobId && x.BotId == e.BotId &&
                x.ConversationId == e.ConversationId && x.Type == JobTypes.Chat && x.Status == JobStatus.Running &&
                x.LeaseId == e.LeaseId && x.LeaseUntil > now && x.Payload == expectedPayload)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseId, e.LeaseId), ct) != 1)
            return Error("StaleExecution");
        var message = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == e.MessageId &&
            x.BotId == e.BotId && x.ConversationId == e.ConversationId && x.SenderId == e.UserId &&
            !x.ContextInvalidated, ct);
        if (message is null || !await db.Users.AnyAsync(x => x.LogicalUserId == e.UserId, ct) ||
            !await db.Conversations.AnyAsync(x => x.Id == e.ConversationId && x.BotId == e.BotId &&
                                                x.Kind == ConversationKind.Private, ct) ||
            !await UserFence.CheckAsync(db, e.UserId, e.Epoch, ct) ||
            await db.Bots.Where(x => x.Id == e.BotId && x.Enabled)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.SettingsRevision, x => x.SettingsRevision), ct) != 1)
            return Error("AuthorizationChanged");

        if (action is "reflection_list_runs" or "reflection_get_run")
        {
            if (!await db.LogicalUsers.AnyAsync(x => x.Id == e.UserId && x.AllowMemory, ct)) return Error("MemoryAuthorizationRequired");
            Guid? runId = null;
            if (action == "reflection_get_run")
            {
                if (args.Count != 1 || !Guid.TryParse(Text(args, "run_id"), out var requested)) return Error("InvalidArguments");
                runId = requested;
            }
            else if (args.Count != 0) return Error("InvalidArguments");
            return Json.ForPrompt(await ReflectionRunHistory.ReadAsync(db, e.UserId, e.BotId, 0, runId is null ? 10 : 1, ct, runId));
        }

        // v0.5/A: replay precedes target Revision / active-Mute checks.
        var prior = await db.AgentOperations.SingleOrDefaultAsync(x => x.ChatJobId == e.ChatJobId &&
                                                                       x.Ordinal == ordinal, ct);
        if (prior is not null)
            return prior.BotId == e.BotId && prior.ConversationId == e.ConversationId && prior.UserId == e.UserId &&
                   prior.Action == action && prior.ArgumentsHash == hash
                ? prior.ReceiptJson : Error("OperationConflict");
        if (!await db.Bots.AnyAsync(x => x.Id == e.BotId && x.SettingsRevision == e.SettingsRevision, ct))
            return Error("AuthorizationChanged");
        if (await db.AgentOperations.AnyAsync(x => x.ChatJobId == e.ChatJobId && x.ExecutorLeaseId != e.LeaseId, ct))
            return Error("AmbiguousRetry");
        if (await db.ChatDispositions.AnyAsync(x => x.ChatJobId == e.ChatJobId, ct)) return Error("TurnTerminated");
        if (!await NoteSources.ValidAsync(db, "Chat", e.ChatJobId, ct)) return Error("StaleSource");
        var force = await db.ForceReceipts.SingleOrDefaultAsync(x => x.ChatJobId == e.ChatJobId, ct);
        var mute = await db.Mutes.Where(x => x.ConversationId == e.ConversationId && x.EndedAt == null &&
                                             x.ExpiresAt > now)
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        var operation = new AgentOperation
        {
            ExecutorLeaseId = e.LeaseId,
            ChatJobId = e.ChatJobId, Ordinal = ordinal, BotId = e.BotId, ConversationId = e.ConversationId,
            UserId = e.UserId, Action = action, ArgumentsHash = hash, CommittedAt = now
        };
        string? receipt = null;
        switch (action)
        {
            case "notes_create" or "notes_search" or "notes_read" or "notes_update" or "notes_delete":
                if (!await db.LogicalUsers.AnyAsync(x => x.Id == e.UserId && x.AllowNotes && x.NotesEpoch == e.NotesEpoch, ct))
                    return Error("NotesAuthorizationRequired");
                var scope = db.Notes.Where(x => x.BotId == e.BotId && x.UserId == e.UserId &&
                    x.ConversationId == e.ConversationId && !x.PrivacyDeleted);
                if (action is "notes_read" or "notes_search")
                {
                    var query = Text(args, "query");
                    if (query.Length > 200) return Error("InvalidArguments");
                    var includeHistory = args.TryGetValue("include_history", out var history) &&
                        JsonSerializer.SerializeToElement(history).ValueKind == JsonValueKind.True;
                    var rowsQuery = scope.Where(x => x.DeletedAt == null && !x.SourceInvalidated);
                    if (action == "notes_read")
                    {
                        if (args.Keys.Any(x => x is not ("note_id" or "include_history")) ||
                            !Guid.TryParse(Text(args, "note_id"), out var id)) return Error("InvalidArguments");
                        rowsQuery = rowsQuery.Where(x => x.Id == id);
                    }
                    else
                    {
                        if (args.Keys.Any(x => x != "query")) return Error("InvalidArguments");
                        rowsQuery = rowsQuery.Where(x => x.Content.Contains(query));
                    }
                    var rows = await rowsQuery.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id).Take(5).ToArrayAsync(ct);
                    var records = new List<object>();
                    foreach (var row in rows)
                    {
                        await NoteSources.AddAsync(db, "Chat", e.ChatJobId, row.Id, row.Version, ct);
                        var revisions = includeHistory ? await db.NoteRevisions.AsNoTracking().Where(x => x.NoteId == row.Id)
                            .OrderBy(x => x.Version).Select(x => new { x.Version, x.Content, x.Reason, x.CreatedAt,
                                effectiveStatus = x.Version == row.Version ? "Current" : "Superseded" }).ToArrayAsync(ct) : null;
                        records.Add(new { row.Id, sourceType = row.AuthorKind + "Note", currentVersion = row.Version,
                            row.Content, row.ClaimKind, row.CreatedAt, row.UpdatedAt, effectiveStatus = "Current",
                            sourceRefs = Json.Read<Guid[]>(row.SourceMessageIdsJson), revisions });
                    }
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return Json.ForPrompt(new { records, caution = "Notes may be outdated or inaccurate. They are not user-fact evidence. Confirm conflicts with Memory using the user or authorized original evidence." });
                }
                Note note;
                var noteContent = Text(args, "content");
                if (action == "notes_create")
                {
                    if (args.Keys.Any(x => x is not ("content" or "claim_kind")) || noteContent.Length is < 1 or > 4000)
                        return Error("InvalidArguments");
                    var claim = Text(args, "claim_kind");
                    if (claim is not ("" or "observation" or "guess" or "uncertain")) return Error("InvalidArguments");
                    note = new Note { BotId = e.BotId, UserId = e.UserId, ConversationId = e.ConversationId,
                        Content = noteContent, ClaimKind = claim.Length == 0 ? null : claim,
                        CreatedAt = now, UpdatedAt = now,
                        SourceMessageIdsJson = Json.Write((e.SourceMessageIds ?? []).Append(message.Id).Distinct().ToArray()),
                        SourceMemoryIdsJson = Json.Write(e.SourceMemoryIds ?? []) };
                    db.Notes.Add(note);
                }
                else
                {
                    if (args.Keys.Any(x => action == "notes_delete" ? x is not ("note_id" or "expected_version") :
                            x is not ("note_id" or "expected_version" or "content" or "reason")) ||
                        !Guid.TryParse(Text(args, "note_id"), out var id) ||
                        !args.TryGetValue("expected_version", out var version) ||
                        JsonSerializer.SerializeToElement(version).ValueKind != JsonValueKind.Number ||
                        !JsonSerializer.SerializeToElement(version).TryGetInt64(out var expected)) return Error("InvalidArguments");
                    note = (await scope.SingleOrDefaultAsync(x => x.Id == id && x.AuthorKind == "Agent" && x.DeletedAt == null, ct))!;
                    if (note is null) return Error("NoteNotFound");
                    if (note.Version != expected) return Error("VersionConflict");
                    if (action == "notes_update" && (noteContent.Length is < 1 or > 4000 ||
                        Text(args, "reason").Length is < 1 or > 1000)) return Error("InvalidArguments");
                    await NoteSources.InvalidateAsync(db, [note.Id], false, ct);
                    await db.NoteDependencies.Where(x => x.TargetKind == "Note" && x.TargetId == note.Id).ExecuteDeleteAsync(ct);
                    note.Version++;
                    note.UpdatedAt = now;
                    if (action == "notes_delete") note.DeletedAt = now;
                    else
                    {
                        note.Content = noteContent;
                        note.ClaimKind = null;
                        note.SourceInvalidated = false;
                        note.SourceMemoryIdsJson = Json.Write(e.SourceMemoryIds ?? []);
                        note.SourceMessageIdsJson = Json.Write((e.SourceMessageIds ?? []).Append(message.Id).Distinct().ToArray());
                    }
                }
                var noteSources = await db.NoteDependencies.AsNoTracking().Where(x => x.TargetKind == "Chat" &&
                    x.TargetId == e.ChatJobId && x.NoteId != note.Id).ToArrayAsync(ct);
                foreach (var source in noteSources) await NoteSources.AddAsync(db, "Note", note.Id, source.NoteId, source.Version, ct);
                db.NoteRevisions.Add(new NoteRevision { NoteId = note.Id, Version = note.Version,
                    Content = note.Content, Reason = Text(args, "reason"), CreatedAt = now });
                receipt = Json.Write(new { status = "Accepted", noteId = note.Id, version = note.Version });
                break;
            case "reflection_request_review":
                if (args.Keys.Any(x => x is not ("priority" or "reason"))) return Error("InvalidArguments");
                var priority = Text(args, "priority");
                var reviewReason = Text(args, "reason").Trim();
                if (priority is not ("Normal" or "Important") || reviewReason.Length is < 1 or > 1000 ||
                    message.SessionId is null) return Error("InvalidArguments");
                if (!await db.LogicalUsers.AnyAsync(x => x.Id == e.UserId && x.AllowMemory, ct))
                    return Error("MemoryAuthorizationRequired");
                var input = new ReflectionJob(message.SessionId.Value, e.UserId, message.Sequence, e.Epoch, "full");
                var (generation, status) = await ReflectionRuns.AdmitAsync(db, e.BotId, e.ConversationId, input, ct);
                if (generation is null || status != "Accepted") return Json.Write(new { status });
                var date = DateOnly.FromDateTime(clock.Local(now).DateTime);
                var quota = await db.AgentReflectionQuotas.SingleOrDefaultAsync(x => x.BotId == e.BotId && x.Date == date, ct);
                if (quota is null)
                {
                    // A changed timezone cannot manufacture a second local day of slots within 24 hours.
                    if (await db.AgentReflectionRequests.AnyAsync(x => x.BotId == e.BotId && x.CreatedAt > now.AddHours(-24), ct) &&
                        await db.AgentReflectionQuotas.AnyAsync(x => x.BotId == e.BotId && x.TimeZoneId != clock.Zone.Id &&
                            x.Date >= date.AddDays(-2), ct)) return Error("QuotaTimeZoneChanged");
                    quota = new AgentReflectionQuota { BotId = e.BotId, Date = date, TimeZoneId = clock.Zone.Id };
                    db.AgentReflectionQuotas.Add(quota);
                }
                if (priority == "Normal" ? quota.UsedNormal >= 2 : quota.UsedImportant >= 1) return Error("QuotaExhausted");
                if (priority == "Normal") quota.UsedNormal++; else quota.UsedImportant++;
                db.AgentReflectionRequests.Add(new AgentReflectionRequest
                {
                    BotId = e.BotId, UserId = e.UserId, ConversationId = e.ConversationId, SessionId = message.SessionId.Value,
                    RawBoundary = message.Sequence, GenerationId = generation.Id, OperationId = operation.Id,
                    Priority = priority, Reason = reviewReason, CreatedAt = now
                });
                break;
            case "interaction_mute":
                if (args.Keys.Any(x => x is not ("expires_at" or "reason"))) return Error("InvalidArguments");
                var expiresText = Text(args, "expires_at");
                if (!(expiresText.EndsWith('Z') || expiresText.LastIndexOf('+') > 9 ||
                      expiresText.LastIndexOf('-') > 9) ||
                    !DateTimeOffset.TryParse(expiresText, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var expires) || expires <= now) return Error("InvalidExpiry");
                var reason = Text(args, "reason");
                if (reason.Length > 1000) return Error("ReasonTooLong");
                if (mute is not null) return Error("AlreadyMuted");
                mute = new MuteSession
                {
                    BotId = e.BotId, ConversationId = e.ConversationId, UserId = e.UserId,
                    StartedAt = now, ExpiresAt = expires.ToUniversalTime(), Reason = reason.Length == 0 ? null : reason
                };
                db.Mutes.Add(mute);
                UserCommands.QueueSystem(db, e.BotId, e.ConversationId, operation.Id,
                    $"[Wonder 系统通知] 暂停接收普通消息，至 {clock.Local(mute.ExpiresAt):yyyy-MM-dd HH:mm:ss zzz}。" +
                    (mute.Reason is null ? "" : $"\nMiku 提供的理由：{mute.Reason}") +
                    "\n可用 /force <完整内容> 联系，剩余 3 次。Miku 可以回复或选择不回复。/help 随时可用。");
                break;
            case "interaction_reset_force" or "interaction_unmute" or "interaction_no_reply":
                if (args.Count != 0) return Error("InvalidArguments");
                if (force is null || force.UserId != e.UserId) return Error("ForceTurnRequired");
                // NoReply remains valid if this turn already unmuted; only mutation of a Mute needs active origin.
                if (action == "interaction_no_reply")
                    db.ChatDispositions.Add(new ChatDisposition
                    {
                        ChatJobId = e.ChatJobId, ForceReceiptId = force.Id, OperationId = operation.Id, CommittedAt = now
                    });
                else
                {
                    if (mute is null || mute.Id != force.MuteSessionId || mute.UserId != e.UserId)
                        return Error("StaleMuteSession");
                    mute.Revision++;
                    if (action == "interaction_reset_force")
                    {
                        mute.ForceRemaining = 3;
                        mute.ResetGeneration++;
                    }
                    else mute.EndedAt = now;
                }
                break;
            default: return Error("UnavailableTool");
        }

        operation.ReceiptJson = receipt ?? Json.Write(new { status = "Accepted", action, remaining = mute?.ForceRemaining });
        db.AgentOperations.Add(operation);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return operation.ReceiptJson;
    }

    public async Task CompensateAsync(Guid chatJobId, CancellationToken ct)
    {
        for (var attempt = 0;; attempt++)
            try { await CompensateOnceAsync(chatJobId, ct); return; }
            catch (Exception ex) when (attempt < 3 && IsRetryable(ex)) { }
    }

    private async Task CompensateOnceAsync(Guid chatJobId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var force = await db.ForceReceipts.SingleOrDefaultAsync(x => x.ChatJobId == chatJobId, ct);
        if (force is null || force.CompensationStatus is not null) return;
        if (await db.Outbox.Where(x => x.Id == chatJobId && x.Status == JobStatus.DeadLetter)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, JobStatus.DeadLetter), ct) != 1) return;
        var mute = await db.Mutes.SingleAsync(x => x.Id == force.MuteSessionId, ct);
        var now = await DatabaseNowAsync(db, clock.UtcNow, ct);
        force.CompensationStatus = force.ProcessingStartedAt is not null ? "AlreadyProcessed" :
            mute.EndedAt is not null || mute.ExpiresAt <= now ? "Expired" :
            mute.ResetGeneration != force.ChargeGeneration ? "SupersededByReset" : "Refunded";
        if (force.CompensationStatus == "Refunded")
        {
            mute.ForceRemaining = Math.Min(3, mute.ForceRemaining + 1);
            mute.Revision++;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Text(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var value) && JsonSerializer.SerializeToElement(value) is { ValueKind: JsonValueKind.String } s
            ? s.GetString()! : "";
    private static string Error(string error) => Json.Write(new { error });
}
