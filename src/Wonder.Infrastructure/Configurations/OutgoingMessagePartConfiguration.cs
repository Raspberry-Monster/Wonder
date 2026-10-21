using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class OutgoingMessagePartConfiguration : IEntityTypeConfiguration<OutgoingMessagePart>
{
    public void Configure(EntityTypeBuilder<OutgoingMessagePart> builder)
    {
        builder.HasIndex(x => new { x.BatchId, x.Sequence }).IsUnique();
        builder.HasOne<OutgoingMessageBatch>().WithMany().HasForeignKey(x => x.BatchId);
    }
}