using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class ReflectionRuns
{
    public static async Task<string?> SignatureAsync(WonderDbContext db, Guid botId, ReflectionJob input,
        CancellationToken ct)
    {
        if (!await db.LogicalUsers.AnyAsync(x => x.Id == input.SubjectId && x.AllowMemory &&
                                                 x.RevocationEpoch == input.Epoch, ct)) return null;
        var rows = await db.Messages.AsNoTracking().Where(x => x.BotId == botId && x.SessionId == input.SessionId &&
                                                               x.SenderId == input.SubjectId &&
                                                               x.Role == MessageRole.User && x.AllowReflection &&
                                                               !x.ContextInvalidated &&
                                                               x.Sequence <= input.Boundary)
            .OrderBy(x => x.Sequence).ThenBy(x => x.Id).Select(x => new { x.Id, x.Text }).ToArrayAsync(ct);
        if (rows.Length == 0) return null;
        return Json.Hash(Json.Write(new { input.Epoch, rows }));
    }

    // Caller owns a short serializable transaction and the user fence. Legacy jobs join lazily.
    public static async Task<(ReflectionGeneration? Run, ReflectionAdmissionOutcome Status)> AdmitAsync(
        WonderDbContext db, Guid botId,
        Guid conversationId, ReflectionJob input, CancellationToken ct, OutboxMessage? legacy = null)
    {
        var signature = await SignatureAsync(db, botId, input, ct);
        if (signature is null) return (null, ReflectionAdmissionOutcome.NoNewEligibleMessages);
        var run = await db.ReflectionGenerations.SingleOrDefaultAsync(x => x.BotId == botId &&
                                                                           x.SessionId == input.SessionId &&
                                                                           x.SubjectId == input.SubjectId &&
                                                                           x.SourceSignature == signature, ct);
        if (run is not null)
        {
            if (run.Status == ReflectionGenerationStatus.Completed && !await db.Reflections.AnyAsync(x =>
                    x.Id == run.ResultId &&
                    x.RevocationEpoch == input.Epoch && x.Payload != "{}", ct))
                return (run, ReflectionAdmissionOutcome.ResultNotReusable);
            if (await db.Outbox.AnyAsync(x => x.Id == run.JobId && x.Status == JobStatus.DeadLetter, ct))
                return (run, ReflectionAdmissionOutcome.FailedRetryExisting);
            return (run, run.Status == ReflectionGenerationStatus.Completed
                ?
                ReflectionAdmissionOutcome.AlreadyProcessed
                :
                run.Status == ReflectionGenerationStatus.Cancelled
                    ? ReflectionAdmissionOutcome.ResultNotReusable
                    : ReflectionAdmissionOutcome.AlreadyScheduled);
        }

        // Old results have no verifiable canonical signature. Never guess equivalence from Boundary.
        if (await db.Reflections.AnyAsync(x => x.SessionId == input.SessionId && x.SubjectId == input.SubjectId &&
                                               x.Kind == ReflectionKind.Full && x.Boundary == input.Boundary, ct))
            return (null, ReflectionAdmissionOutcome.ResultNotReusable);
        var legacyBoundary = await db.Reflections.Where(x =>
                x.SessionId == input.SessionId && x.SubjectId == input.SubjectId &&
                x.Kind == ReflectionKind.Full && !db.ReflectionGenerations.Any(g => g.ResultId == x.Id))
            .MaxAsync(x => (long?)x.Boundary, ct);
        if (legacyBoundary is { } prior && !await db.Messages.AnyAsync(x => x.SessionId == input.SessionId &&
                                                                            x.SenderId == input.SubjectId &&
                                                                            x.Role == MessageRole.User &&
                                                                            x.AllowReflection &&
                                                                            !x.ContextInvalidated &&
                                                                            x.Sequence > prior &&
                                                                            x.Sequence <= input.Boundary, ct))
            return (null, ReflectionAdmissionOutcome.ResultNotReusable);
        var job = legacy ?? Jobs.Add(db, botId, JobKind.Reflection, input, conversationId);
        run = new ReflectionGeneration
        {
            BotId = botId, SessionId = input.SessionId, SubjectId = input.SubjectId, Epoch = input.Epoch,
            RawBoundary = input.Boundary, SourceSignature = signature, JobId = job.Id
        };
        db.ReflectionGenerations.Add(run);
        return (run, ReflectionAdmissionOutcome.Accepted);
    }

    public static void Cancel(ReflectionGeneration? run, ReflectionCancellationReason reason)
    {
        if (run is null) return;
        run.Status = ReflectionGenerationStatus.Cancelled;
        run.CancellationReason = reason;
    }
}