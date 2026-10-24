using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Features.Memory;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class MemoryQueries
{
    public static IQueryable<Memory> RetiredGroupSources(WonderDbContext db) => db.Memories.Where(m =>
        (m.Scope != MemoryScope.Private && m.Scope != MemoryScope.UserShared) ||
        (m.Scope == MemoryScope.UserShared && db.Memories.Any(s => s.Id == m.SharedFromMemoryId && s.Scope != MemoryScope.Private)));

    public static IQueryable<Memory> Authorized(WonderDbContext db, MemoryAccess access)
    {
        if (access.GroupId is not null) return db.Memories.Where(x => false);
        return db.Memories.AsNoTracking().Where(MemoryRules.Authorized(access)).Where(m =>
            (m.Scope == MemoryScope.Private || m.Scope == MemoryScope.UserShared) &&
            (m.Scope != MemoryScope.UserShared && m.Scope != MemoryScope.SpaceShared) ||
            (db.LogicalUsers.Any(u => u.Id == m.SubjectId && u.AllowMemory && u.AllowCrossGroupMemory) &&
             db.Memories.Any(source => source.Id == m.SharedFromMemoryId && source.Status != MemoryStatus.Revoked &&
                                       source.ArchivedAt == null &&
                                       source.Scope == MemoryScope.Private &&
                                       source.SubjectId == m.SubjectId && source.BotId == m.BotId &&
                                       (m.Scope != MemoryScope.SpaceShared || (source.Scope == MemoryScope.GroupUser &&
                                                                               db.GroupPolicies.Any(g =>
                                                                                   g.BotId == m.BotId &&
                                                                                   g.GroupId == source.ScopeOwnerId &&
                                                                                   g.ContextSpaceId != null &&
                                                                                   g.ContextSpaceId.ToString()!
                                                                                       .ToLower() ==
                                                                                   m.ScopeOwnerId.ToLower()))))));
    }

    public static IQueryable<Memory> Nearest(WonderDbContext db, MemoryAccess access, Guid profileId,
        float[] vector)
    {
        var authorized = Authorized(db, access);
        var ranked = Ranked(db, authorized, profileId, vector);
        return (from memory in authorized
            join distance in ranked on memory.Id equals distance.MemoryId
            orderby distance.Distance, memory.Id
            select memory).Take(5);
    }

    public static IQueryable<VectorDistance> Ranked(WonderDbContext db, IQueryable<Memory> scope,
        Guid profileId, float[] vector)
    {
        var query = new Vector(vector);
        // Scope authorization stays in SQL before ordering and applying Top-K.
        var distances = db.Embeddings.Where(e => e.ProfileId == profileId)
            .Select(e => new VectorDistance { MemoryId = e.MemoryId, Distance = e.Values.CosineDistance(query) });
        return from memory in scope
            join distance in distances on memory.Id equals distance.MemoryId
            orderby distance.Distance, memory.Id
            select distance;
    }

    public sealed class VectorDistance
    {
        public Guid MemoryId { get; set; }
        public double Distance { get; set; }
    }
}
