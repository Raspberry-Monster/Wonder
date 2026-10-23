using Microsoft.EntityFrameworkCore;
using Pgvector;
using Wonder.Infrastructure.Configurations;

namespace Wonder.Infrastructure;

[EntityTypeConfiguration(typeof(MemoryEmbeddingConfiguration))]
public sealed class MemoryEmbedding
{
    public Guid MemoryId { get; set; }
    public Guid ProfileId { get; set; }
    public Vector Values { get; set; } = new(Array.Empty<float>());
}