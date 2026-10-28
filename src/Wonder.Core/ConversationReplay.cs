namespace Wonder.Core;

// An internal, replaceable Chat transport checkpoint, never a memory/evidence source.
public sealed class ConversationReplayState
{
    public Guid ConversationId { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public int FormatVersion { get; set; } = 1;
    public long Version { get; set; } = 1;
    public long ThroughSequence { get; set; }
    public Guid FinalMessageId { get; set; }
    public string Fingerprint { get; set; } = "";
    public string SummaryBaseline { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ConversationReplayDependency
{
    public Guid ConversationId { get; set; }
    public string Kind { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Version { get; set; } = "";
}
