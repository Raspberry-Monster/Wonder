using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.Property(x => x.Sequence).UseIdentityAlwaysColumn();
        builder.Property(x => x.LeaseId).IsConcurrencyToken();
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt, x.LeaseUntil });
        builder.HasIndex(x => new { x.ConversationId, x.Sequence });
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}
