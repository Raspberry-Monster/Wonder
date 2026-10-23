using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class GlobalEmotionStateConfiguration : IEntityTypeConfiguration<GlobalEmotionState>
{
    public void Configure(EntityTypeBuilder<GlobalEmotionState> builder)
    {
        builder.HasKey(x => x.BotId);
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.ToTable(t => t.HasCheckConstraint("CK_Mood_Range",
            "\"Valence\" BETWEEN -1 AND 1 AND \"Arousal\" BETWEEN 0 AND 1 AND \"Energy\" BETWEEN 0 AND 1"));
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}