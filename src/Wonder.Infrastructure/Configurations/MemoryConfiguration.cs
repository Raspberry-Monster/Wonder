using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryConfiguration : IEntityTypeConfiguration<Memory>
{
    public void Configure(EntityTypeBuilder<Memory> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.SubjectId, x.Status });
        builder.HasIndex(x => new { x.SubjectId, x.FactKey });
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasIndex(x => x.SupersedesMemoryId);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}
