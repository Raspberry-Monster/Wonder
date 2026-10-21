using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class EmotionEventConfiguration : IEntityTypeConfiguration<EmotionEvent>
{
    public void Configure(EntityTypeBuilder<EmotionEvent> builder)
    {
        builder.HasIndex(x => new { x.BotId, x.Sequence }).IsUnique();
        builder.HasIndex(x => new { x.SourceReflectionId, x.SourceEventKey }).IsUnique();
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}