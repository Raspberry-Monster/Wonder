using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ConversationSummaryConfiguration : IEntityTypeConfiguration<ConversationSummary>
{
    public void Configure(EntityTypeBuilder<ConversationSummary> builder)
    {
        builder.HasIndex(x => x.SessionId).IsUnique();
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.Property(x => x.ProjectionRevision).IsConcurrencyToken();
        builder.Property(x => x.ProjectionState).HasMaxLength(16);
        builder.HasIndex(x => x.ProjectionExpiresAt);
        builder.Property(x => x.MessageIdsJson).HasDefaultValue("[]");
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}
