using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class RelationshipObservationConfiguration : IEntityTypeConfiguration<RelationshipObservation>
{
    public void Configure(EntityTypeBuilder<RelationshipObservation> builder)
    {
        builder.HasIndex(x => new { x.ReflectionId, x.Candidate }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}