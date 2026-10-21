using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class OutgoingMessageBatchConfiguration : IEntityTypeConfiguration<OutgoingMessageBatch>
{
    public void Configure(EntityTypeBuilder<OutgoingMessageBatch> builder)
    {
        builder.HasIndex(x => x.SourceJobId);
        builder.HasOne<BotInstance>().WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Restrict);
    }
}