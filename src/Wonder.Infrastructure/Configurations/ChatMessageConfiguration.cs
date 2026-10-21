using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> builder)
    {
        builder.Property(x => x.Sequence).UseIdentityAlwaysColumn();
        builder.HasIndex(x => new { x.BotId, x.Platform, x.ExternalMessageId }).IsUnique();
        builder.HasIndex(x => new { x.ConversationId, x.Sequence });
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ConversationSession>().WithMany().HasForeignKey(x => x.SessionId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}