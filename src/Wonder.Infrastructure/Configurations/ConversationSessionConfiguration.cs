using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ConversationSessionConfiguration : IEntityTypeConfiguration<ConversationSession>
{
    public void Configure(EntityTypeBuilder<ConversationSession> builder)
    {
        builder.HasIndex(x => new { x.Status, x.DeadlineAt });
        builder.HasIndex(x => x.ConversationId).IsUnique().HasFilter("\"Status\" = 0");
        builder.Property(x => x.Revision).IsConcurrencyToken();
        builder.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}