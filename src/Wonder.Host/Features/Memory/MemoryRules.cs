using System.Linq.Expressions;
using Wonder.Core;

namespace Wonder.Host.Features.Memory;

public static class MemoryRules
{
    public static Expression<Func<Core.Memory, bool>> Authorized(MemoryAccess access)
    {
        return memory => access.AllowPersonal && memory.BotId == access.BotId &&
                         memory.SubjectId == access.UserId &&
                         memory.Status != MemoryStatus.Revoked && memory.ArchivedAt == null && !memory.IsOutdated &&
                         memory.Text != "";
    }

    public static bool CanAdmit(MemoryCandidate candidate, bool consent, long sourceEpoch, long currentEpoch,
        IReadOnlySet<Guid> allowedEvidence)
    {
        return consent && sourceEpoch == currentEpoch && candidate.Explicit && !candidate.Sensitive &&
               !string.IsNullOrWhiteSpace(candidate.Text) && !string.IsNullOrWhiteSpace(candidate.FactKey) &&
               candidate.Text.Length is > 0 and <= 1000 && candidate.FactKey.Length is > 0 and <= 150 &&
               candidate.EvidenceMessageIds.Length > 0 && candidate.EvidenceMessageIds.All(allowedEvidence.Contains);
    }
}