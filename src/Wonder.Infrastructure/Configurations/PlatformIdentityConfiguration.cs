using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class PlatformIdentityConfiguration : IEntityTypeConfiguration<PlatformIdentity>
{
    public void Configure(EntityTypeBuilder<PlatformIdentity> builder)
    {
        builder.HasIndex(x => new { x.Platform, x.ExternalId }).IsUnique();
        builder.HasOne<LogicalUser>().WithMany().HasForeignKey(x => x.UserId);
    }
}