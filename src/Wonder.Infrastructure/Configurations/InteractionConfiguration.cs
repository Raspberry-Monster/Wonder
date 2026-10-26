using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class MuteSessionConfiguration : IEntityTypeConfiguration<MuteSession>
{
    public void Configure(EntityTypeBuilder<MuteSession> b)
    {
        b.HasIndex(x => new { x.ConversationId, x.ExpiresAt });
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.ToTable(t =>
            t.HasCheckConstraint("CK_Mute_ForceRemaining", "\"ForceRemaining\" >= 0 AND \"ForceRemaining\" <= 3"));
        b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ForceReceiptConfiguration : IEntityTypeConfiguration<ForceReceipt>
{
    public void Configure(EntityTypeBuilder<ForceReceipt> b)
    {
        b.HasIndex(x => new { x.BotId, x.PlatformEventKey }).IsUnique();
        b.HasIndex(x => x.ChatJobId).IsUnique();
        b.HasOne<MuteSession>().WithMany().HasForeignKey(x => x.MuteSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AgentOperationConfiguration : IEntityTypeConfiguration<AgentOperation>
{
    public void Configure(EntityTypeBuilder<AgentOperation> b)
    {
        b.HasIndex(x => new { x.ChatJobId, x.Ordinal }).IsUnique();
    }
}

public sealed class ChatDispositionConfiguration : IEntityTypeConfiguration<ChatDisposition>
{
    public void Configure(EntityTypeBuilder<ChatDisposition> b)
    {
        b.HasKey(x => x.ChatJobId);
        b.HasIndex(x => x.OperationId).IsUnique();
    }
}