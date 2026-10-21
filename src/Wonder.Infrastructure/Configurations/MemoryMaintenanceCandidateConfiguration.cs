using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryMaintenanceCandidateConfiguration : IEntityTypeConfiguration<MemoryMaintenanceCandidate>
{
    public void Configure(EntityTypeBuilder<MemoryMaintenanceCandidate> builder)
    {
        builder.HasIndex(x => new { x.SourceReflectionId, x.CandidateHash }).IsUnique();
        builder.HasIndex(x => new { x.SubjectId, x.Status });
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}