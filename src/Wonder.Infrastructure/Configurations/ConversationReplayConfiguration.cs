using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class ConversationReplayConfiguration : IEntityTypeConfiguration<ConversationReplayState>
{
    public void Configure(EntityTypeBuilder<ConversationReplayState> b)
    {
        b.HasKey(x => x.ConversationId);
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.ExpiresAt);
        b.HasIndex(x => x.UserId);
        b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ConversationReplayDependencyConfiguration : IEntityTypeConfiguration<ConversationReplayDependency>
{
    public void Configure(EntityTypeBuilder<ConversationReplayDependency> b)
    {
        b.HasKey(x => new { x.ConversationId, x.Kind, x.ResourceId, x.Version });
        b.HasIndex(x => new { x.Kind, x.ResourceId });
        b.HasOne<ConversationReplayState>().WithMany().HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
