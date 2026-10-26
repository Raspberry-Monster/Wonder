using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class RelationshipStateConfiguration : IEntityTypeConfiguration<RelationshipState>
{
    public void Configure(EntityTypeBuilder<RelationshipState> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.UserId }).IsUnique();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}