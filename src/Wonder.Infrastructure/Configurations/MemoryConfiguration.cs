using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryConfiguration : IEntityTypeConfiguration<Core.Memory>
{
    public void Configure(EntityTypeBuilder<Core.Memory> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.Scope, x.ScopeOwnerId, x.Status });
        builder.HasIndex(x => new { x.SubjectId, x.FactKey });
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => x.SupersedesMemoryId);
        builder.HasOne<Core.Memory>().WithMany().HasForeignKey(x => x.SharedFromMemoryId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.SharedFromMemoryId, x.Scope, x.ScopeOwnerId })
            .IsUnique().HasFilter("\"SharedFromMemoryId\" IS NOT NULL AND \"Status\" <> 2");
        builder.ToTable(t =>
            t.HasCheckConstraint("CK_Memory_Enums", "\"Scope\" BETWEEN 0 AND 5 AND \"Status\" BETWEEN 0 AND 2"));
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}