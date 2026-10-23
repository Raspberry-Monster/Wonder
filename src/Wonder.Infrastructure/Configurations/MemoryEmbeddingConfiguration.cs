using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryEmbeddingConfiguration : IEntityTypeConfiguration<MemoryEmbedding>
{
    public void Configure(EntityTypeBuilder<MemoryEmbedding> builder)
    {
        builder.HasKey(x => new { x.MemoryId, x.ProfileId });
        builder.Property(x => x.Values).HasColumnType("vector");
        builder.HasOne<Memory>().WithMany().HasForeignKey(x => x.MemoryId);
        builder.HasOne<EmbeddingProfile>().WithMany().HasForeignKey(x => x.ProfileId);
    }
}