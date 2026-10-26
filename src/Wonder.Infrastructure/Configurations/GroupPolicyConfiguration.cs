using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class GroupPolicyConfiguration : IEntityTypeConfiguration<GroupPolicy>
{
    public void Configure(EntityTypeBuilder<GroupPolicy> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.GroupId }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}