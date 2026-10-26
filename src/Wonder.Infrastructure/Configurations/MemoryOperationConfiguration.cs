using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryOperationConfiguration : IEntityTypeConfiguration<MemoryOperation>
{
    public void Configure(EntityTypeBuilder<MemoryOperation> builder)
    {
        builder.HasIndex(x => new { x.ActorId, x.IdempotencyKey }).IsUnique();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(128);
        builder.Property(x => x.ArgumentsHash).HasMaxLength(64);
        builder.Property(x => x.Kind).HasMaxLength(16);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => new { x.Stage, x.CreatedAt });
        builder.HasOne<LogicalUser>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MemoryOperationItemConfiguration : IEntityTypeConfiguration<MemoryOperationItem>
{
    public void Configure(EntityTypeBuilder<MemoryOperationItem> builder)
    {
        builder.HasIndex(x => new { x.OperationId, x.Kind, x.ResourceId }).IsUnique();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne<MemoryOperation>().WithMany().HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EvidenceSuppressionConfiguration : IEntityTypeConfiguration<EvidenceSuppression>
{
    public void Configure(EntityTypeBuilder<EvidenceSuppression> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.SubjectId, x.MessageId });
        builder.HasIndex(x => new { x.OperationId, x.MemoryId, x.MessageId }).IsUnique();
        builder.HasOne<MemoryOperation>().WithMany().HasForeignKey(x => x.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RelationshipClaimConfiguration : IEntityTypeConfiguration<RelationshipClaim>
{
    public void Configure(EntityTypeBuilder<RelationshipClaim> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.SubjectId });
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne<RelationshipState>().WithMany().HasForeignKey(x => x.RelationshipId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}