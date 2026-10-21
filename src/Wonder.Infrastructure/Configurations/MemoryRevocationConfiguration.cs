using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MemoryRevocationConfiguration : IEntityTypeConfiguration<MemoryRevocation>
{
    public void Configure(EntityTypeBuilder<MemoryRevocation> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.UserId, x.Epoch });
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}