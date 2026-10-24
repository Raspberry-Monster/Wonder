using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wonder.Core;

namespace Wonder.Infrastructure.Configurations;

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> b)
    {
        b.HasIndex(x => new { x.BotId, x.UserId, x.ConversationId });
        b.Property(x => x.Version).IsConcurrencyToken();
    }
}
public sealed class NoteRevisionConfiguration : IEntityTypeConfiguration<NoteRevision>
{
    public void Configure(EntityTypeBuilder<NoteRevision> b) => b.HasKey(x => new { x.NoteId, x.Version });
}
public sealed class NoteDependencyConfiguration : IEntityTypeConfiguration<NoteDependency>
{
    public void Configure(EntityTypeBuilder<NoteDependency> b)
    {
        b.HasKey(x => new { x.NoteId, x.Version, x.TargetKind, x.TargetId });
        b.HasIndex(x => new { x.TargetKind, x.TargetId });
    }
}
