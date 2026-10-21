using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class PlatformBindingConfiguration : IEntityTypeConfiguration<PlatformBinding>
{
    public void Configure(EntityTypeBuilder<PlatformBinding> builder)
    {
        builder.HasIndex(x => x.BotId).IsUnique();
        builder.HasIndex(x => new { x.AdapterType, x.PlatformAccountId }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}