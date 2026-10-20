using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Memory;

namespace Wonder.Infrastructure;

public static class MemoryQueries
{
    public static IQueryable<Core.Memory> Authorized(WonderDbContext db, MemoryAccess access)
    {
        return db.Memories.AsNoTracking().Where(MemoryRules.Authorized(access)).Where(m =>
            (m.Scope != MemoryScope.UserShared && m.Scope != MemoryScope.SpaceShared) ||
            (db.LogicalUsers.Any(u => u.Id == m.SubjectId && u.AllowMemory && u.AllowCrossGroupMemory) &&
             db.Memories.Any(source => source.Id == m.SharedFromMemoryId && source.Status != MemoryStatus.Revoked &&
                 source.SubjectId == m.SubjectId && source.BotId == m.BotId &&
                 (m.Scope != MemoryScope.SpaceShared || (source.Scope == MemoryScope.GroupUser &&
                     db.GroupPolicies.Any(g => g.BotId == m.BotId && g.GroupId == source.ScopeOwnerId &&
                         g.ContextSpaceId != null && g.ContextSpaceId.ToString()!.ToLower() == m.ScopeOwnerId.ToLower()))))));
    }

    public static IQueryable<Core.Memory> Nearest(WonderDbContext db, MemoryAccess access, Guid profileId,
        float[] vector)
    {
        var values = "[" + string.Join(",", vector.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
        // Only the pgvector cast/operator needs SQL with the current real[] storage mapping.
        // No Top-K is applied here: EF composes scope authorization before ordering and Take.
        var distances = db.Database.SqlQuery<VectorDistance>($"""
                                                              SELECT e."MemoryId", e."Values"::vector <=> {values}::vector AS "Distance"
                                                              FROM "Embeddings" e WHERE e."ProfileId" = {profileId}
                                                              """);
        var authorized = Authorized(db, access);
        return (from memory in authorized
            join distance in distances on memory.Id equals distance.MemoryId
            orderby distance.Distance, memory.Id
            select memory).Take(5);
    }

    private sealed class VectorDistance
    {
        public Guid MemoryId { get; set; }
        public double Distance { get; set; }
    }
}