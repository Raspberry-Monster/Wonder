using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ContextSpaceConfiguration : IEntityTypeConfiguration<ContextSpace>
{
    public void Configure(EntityTypeBuilder<ContextSpace> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}