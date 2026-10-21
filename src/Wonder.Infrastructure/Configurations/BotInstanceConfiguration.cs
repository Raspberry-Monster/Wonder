using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class BotInstanceConfiguration : IEntityTypeConfiguration<BotInstance>
{
    public void Configure(EntityTypeBuilder<BotInstance> builder)
    {
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.SettingsRevision).IsConcurrencyToken();
        builder.ToTable(t => t.HasCheckConstraint("CK_Bot_Settings",
            "\"MessageRetentionDays\" BETWEEN 1 AND 3650 AND \"PrivateIdleMinutes\" BETWEEN 1 AND 1440 AND \"GroupIdleMinutes\" BETWEEN 1 AND 1440 AND \"DailyTokenLimit\" > 0 AND \"MonthlyTokenLimit\" > 0"));
    }
}