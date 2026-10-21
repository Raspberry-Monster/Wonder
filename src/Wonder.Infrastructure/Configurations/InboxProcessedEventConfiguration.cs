using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class InboxProcessedEventConfiguration : IEntityTypeConfiguration<InboxProcessedEvent>
{
    public void Configure(EntityTypeBuilder<InboxProcessedEvent> builder)
    {
        builder.HasKey(x => new { x.EventId, x.HandlerName });
    }
}