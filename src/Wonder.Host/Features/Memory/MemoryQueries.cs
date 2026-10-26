using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Features.Memory;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class MemoryQueries
{
    public static IQueryable<Memory> Authorized(WonderDbContext db, MemoryAccess access)
    {
        return db.Memories.AsNoTracking().Where(MemoryRules.Authorized(access))
            .Where(m => db.LogicalUsers.Any(u => u.Id == m.SubjectId && u.AllowMemory));
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
        var distances = db.Embeddings.Where(e => e.ProfileId == profileId &&
                db.Memories.Any(m => m.Id == e.MemoryId && m.Revision == e.SourceRevision))
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
