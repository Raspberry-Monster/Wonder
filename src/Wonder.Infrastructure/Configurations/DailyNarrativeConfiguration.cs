using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class DailyNarrativeConfiguration : IEntityTypeConfiguration<DailyNarrative>
{
    public void Configure(EntityTypeBuilder<DailyNarrative> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.Date }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}