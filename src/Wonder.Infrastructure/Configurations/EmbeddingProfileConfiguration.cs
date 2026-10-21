using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class EmbeddingProfileConfiguration : IEntityTypeConfiguration<EmbeddingProfile>
{
    public void Configure(EntityTypeBuilder<EmbeddingProfile> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.Provider, x.Model, x.Dimensions, x.Version })
            .IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}