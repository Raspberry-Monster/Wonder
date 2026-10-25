using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class ReflectionRunHistory
{
    public static async Task<object[]> ReadAsync(WonderDbContext db, Guid userId, Guid? botId, int skip, int take,
        CancellationToken ct, Guid? runId = null)
    {
        var runs = await (from g in db.ReflectionGenerations.AsNoTracking()
            join j in db.Outbox on g.JobId equals j.Id
            where g.SubjectId == userId && (botId == null || g.BotId == botId) && (runId == null || g.Id == runId)
            orderby g.CreatedAt descending, g.Id
            select new { g.Id, g.BotId, g.RawBoundary, g.Status, taskStatus = j.Status, g.ResultId,
                g.CancellationReason, g.CreatedAt, j.ErrorCode, g.JobId }).Skip(skip).Take(take).ToArrayAsync(ct);
        var ids = runs.Select(x => x.Id).ToArray();
        var jobs = runs.Select(x => x.JobId).ToArray();
        var results = runs.Where(x => x.ResultId != null).Select(x => x.ResultId!.Value).ToArray();
        var requests = await db.AgentReflectionRequests.AsNoTracking().Where(x => x.UserId == userId && ids.Contains(x.GenerationId))
            .Select(x => new { x.GenerationId, x.Reason, x.Priority }).ToArrayAsync(ct);
        var downstream = await db.Outbox.AsNoTracking().Where(x => x.CausationId != null && jobs.Contains(x.CausationId.Value))
            .Select(x => new { x.CausationId, x.Type, x.Status, x.ErrorCode }).ToArrayAsync(ct);
        var maintenance = await db.MemoryCandidates.AsNoTracking().Where(x => x.SubjectId == userId && results.Contains(x.SourceReflectionId))
            .GroupBy(x => new { x.SourceReflectionId, x.Status })
            .Select(x => new { x.Key.SourceReflectionId, x.Key.Status, count = x.Count() }).ToArrayAsync(ct);
        var proposals = await db.Reflections.AsNoTracking().Where(x => x.SubjectId == userId && results.Contains(x.Id))
            .Select(x => new { x.Id, x.Payload }).ToArrayAsync(ct);
        int? ProposalCount(Guid? result)
        {
            var payload = proposals.FirstOrDefault(x => x.Id == result)?.Payload;
            if (payload is null or "{}") return null;
            try
            {
                var p = Json.Read<ReflectionProposal>(payload);
                return p.Relationships is null || p.Memories is null ? null :
                    (p.Emotion is null ? 0 : 1) + p.Relationships.Length + p.Memories.Length;
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return runs.Select(x => (object)new { x.Id, x.BotId, x.RawBoundary,
            status = x.Status == ReflectionGenerationStatus.Completed ? ReflectionRunDisplayStatus.ReflectionCompleted : x.Status == ReflectionGenerationStatus.Cancelled || x.taskStatus == JobStatus.Cancelled ? ReflectionRunDisplayStatus.Cancelled :
                x.taskStatus == JobStatus.DeadLetter ? ReflectionRunDisplayStatus.Failed : x.taskStatus == JobStatus.Running ? ReflectionRunDisplayStatus.Running : ReflectionRunDisplayStatus.Queued,
            x.taskStatus, x.ResultId, x.CancellationReason, x.CreatedAt, x.ErrorCode,
            proposalCount = ProposalCount(x.ResultId),
            memoryCandidateCount = maintenance.Where(m => m.SourceReflectionId == x.ResultId).Sum(m => m.count),
            requests = requests.Where(r => r.GenerationId == x.Id).Select(r => new { r.Priority, r.Reason }),
            downstream = downstream.Where(d => d.CausationId == x.JobId).Select(d => new { d.Type, d.Status, d.ErrorCode }),
            memoryMaintenance = maintenance.Where(m => m.SourceReflectionId == x.ResultId).Select(m => new { m.Status, m.count })
        }).ToArray();
    }
}
