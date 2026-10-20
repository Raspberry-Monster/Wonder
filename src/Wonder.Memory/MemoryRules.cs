using System.Linq.Expressions;
using Wonder.Core;

namespace Wonder.Memory;

public static class MemoryRules
{
    public static Expression<Func<Core.Memory, bool>> Authorized(MemoryAccess access)
    {
        var space = access.ContextSpaceId?.ToString();
        var user = access.UserId.ToString();
        return memory => memory.BotId == access.BotId && memory.Status != MemoryStatus.Revoked &&
            ((access.GroupId == null
                ? access.AllowPersonal && memory.SubjectId == access.UserId && memory.Scope == MemoryScope.Private
                : (access.AllowPersonal && memory.Scope == MemoryScope.GroupUser && memory.SubjectId == access.UserId && memory.ScopeOwnerId == access.GroupId)
                  || (memory.Scope == MemoryScope.GroupShared && memory.ScopeOwnerId == access.GroupId)
                  || memory.Scope == MemoryScope.GlobalPublic)
             || (access.AllowPersonal && access.AllowUserShared && memory.Scope == MemoryScope.UserShared &&
                 memory.SubjectId == access.UserId && memory.ScopeOwnerId == user)
             || (access.GroupId != null && space != null && memory.Scope == MemoryScope.SpaceShared && memory.ScopeOwnerId == space));
    }

    public static bool CanAdmit(MemoryCandidate candidate, bool consent, long sourceEpoch, long currentEpoch,
        IReadOnlySet<Guid> allowedEvidence)
    {
        return consent && sourceEpoch == currentEpoch && candidate.Explicit && !candidate.Sensitive &&
               candidate.Text.Length is > 0 and <= 1000 && candidate.FactKey.Length is > 0 and <= 150 &&
               candidate.EvidenceMessageIds.Length > 0 && candidate.EvidenceMessageIds.All(allowedEvidence.Contains);
    }
}
