namespace Wonder.Core;

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public NoteAuthorKind AuthorKind { get; set; } = NoteAuthorKind.Agent;
    public string Content { get; set; } = "";
    public NoteClaimKind? ClaimKind { get; set; }
    public string SourceMessageIdsJson { get; set; } = "[]";
    public string SourceMemoryIdsJson { get; set; } = "[]";
    public bool SourceInvalidated { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public bool PrivacyDeleted { get; set; }
}

public sealed class NoteRevision
{
    public Guid NoteId { get; set; }
    public long Version { get; set; }
    public string Content { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class NoteDependency
{
    public Guid NoteId { get; set; }
    public long Version { get; set; }
    public NoteDependencyTargetKind TargetKind { get; set; } = NoteDependencyTargetKind.Chat;
    public Guid TargetId { get; set; }
}
