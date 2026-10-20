namespace Wonder.Core;

public enum ConversationKind
{
    Private,
    Group
}

public enum SessionStatus
{
    Active,
    Closing,
    Reflecting,
    Closed
}

public enum MemoryScope
{
    Private,
    GroupUser,
    GroupShared,
    GlobalPublic,
    UserShared,
    SpaceShared
}

public enum MemoryStatus
{
    PendingEmbedding,
    Active,
    Revoked
}

public enum TaskKind
{
    Chat,
    LightReflection,
    StrongReflection,
    DailyNarrative,
    RollingSummary,
    Embedding
}

public enum DeliveryStatus
{
    Pending,
    Sending,
    Sent,
    DeliveryUnknown,
    Failed
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    DeadLetter
}

public enum SegmentKind
{
    Text,
    Image,
    Mention,
    ReplyReference
}

public sealed record MessageSegment(SegmentKind Kind, string Value);

public sealed record NormalizedEvent(
    string AccountId,
    string MessageId,
    string SenderId,
    string ConversationId,
    ConversationKind Kind,
    IReadOnlyList<MessageSegment> Segments,
    DateTimeOffset OccurredAt);

public sealed record TriggerDecision(bool ShouldPersist, bool ShouldRespond, bool ShouldReflect);

public sealed record ContextSnapshot(
    Guid BotInstanceId,
    Guid SessionId,
    long IdentityRevision,
    long EmotionRevision,
    long? DailyNarrativeRevision,
    long RelationshipRevision,
    long SummaryRevision,
    IReadOnlyList<Guid> RetrievedMemoryIds,
    string ModelProfileId,
    IReadOnlyList<Guid>? SourceMessageIds = null, IReadOnlyList<Guid>? SourceUserIds = null);

public sealed record EmotionProposal(
    double Valence,
    double Arousal,
    double Energy,
    string Reason,
    Guid[] EvidenceMessageIds);

public sealed record RelationshipProposal(string Preference, bool Explicit, Guid[] EvidenceMessageIds);

public sealed record MemoryCandidate(
    string FactKey,
    string Text,
    bool Explicit,
    bool Sensitive,
    Guid[] EvidenceMessageIds);

public sealed record ReflectionProposal(
    bool NeedsStrongReview,
    EmotionProposal? Emotion,
    RelationshipProposal[] Relationships,
    MemoryCandidate[] Memories);

public sealed record MemoryAccess(Guid BotId, Guid UserId, string? GroupId,
    Guid? ContextSpaceId = null, bool AllowUserShared = false, bool AllowPersonal = true);

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public sealed class BotInstance : Entity
{
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "Wonder";
    public bool Enabled { get; set; } = true;
    public long SettingsRevision { get; set; } = 1;
    public int MessageRetentionDays { get; set; } = 90;
    public int PrivateIdleMinutes { get; set; } = 10;
    public int GroupIdleMinutes { get; set; } = 10;
    public bool PersistMessages { get; set; } = true;
    public long DailyTokenLimit { get; set; } = 100_000;
    public long MonthlyTokenLimit { get; set; } = 2_000_000;
}

public sealed class LogicalUser : Entity
{
    public bool AllowMemory { get; set; }
    public bool AllowCrossGroupMemory { get; set; }
    public string PreferredName { get; set; } = "";
    public long RevocationEpoch { get; set; }
}

public sealed class PlatformIdentity : Entity
{
    public Guid UserId { get; set; }
    public string Platform { get; set; } = "qq";
    public string ExternalId { get; set; } = "";
}

public sealed class PlatformBinding : Entity
{
    public Guid BotId { get; set; }
    public string AdapterType { get; set; } = "onebot11";
    public string PlatformAccountId { get; set; } = "";
    public string WebSocketUrl { get; set; } = "";
    public string ProtectedAccessToken { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class BindingChallenge : Entity
{
    public Guid ApplicationUserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ContextSpace : Entity
{
    public Guid BotId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class GroupPolicy : Entity
{
    public Guid BotId { get; set; }
    public string GroupId { get; set; } = "";
    public Guid? ContextSpaceId { get; set; }
    public bool RespondToMention { get; set; } = true;
    public bool RespondToReply { get; set; } = true;
    public string CommandPrefix { get; set; } = "/wonder";
    public bool PersistMessages { get; set; } = true;

    public bool AllowReflection { get; set; } = true;

    // Proactive messages are deliberately opt-in; no unsolicited scheduler in v0.1.
    public bool ProactiveEnabled { get; set; }
}

public sealed class PersonaIdentityRevision : Entity
{
    public Guid BotId { get; set; }
    public long Revision { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Conversation : Entity
{
    public Guid BotId { get; set; }
    public string Platform { get; set; } = "qq";
    public string ExternalId { get; set; } = "";
    public ConversationKind Kind { get; set; }
}

public sealed class ConversationSession : Entity
{
    public Guid BotId { get; set; }
    public Guid ConversationId { get; set; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset DeadlineAt { get; set; }
    public long StartingMoodRevision { get; set; }
    public long Revision { get; set; } = 1;
    public long? ClosingBoundary { get; set; }
}

public sealed class ChatMessage : Entity
{
    public long Sequence { get; set; }
    public Guid BotId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? SenderId { get; set; }
    public string Platform { get; set; } = "qq";
    public string ExternalMessageId { get; set; } = "";
    public string Role { get; set; } = "user";
    public long? InReplyToSequence { get; set; }
    public string SegmentsJson { get; set; } = "[]";
    public string Text { get; set; } = "";
    public string? ContextSnapshotJson { get; set; }
    public bool ContextInvalidated { get; set; }
    public bool AllowReflection { get; set; }
    public bool ShouldRespond { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ConversationSummary : Entity
{
    public Guid BotId { get; set; }
    public Guid SessionId { get; set; }
    public long ThroughSequence { get; set; }
    public string SubjectIdsJson { get; set; } = "[]";
    public string MemoryIdsJson { get; set; } = "[]";
    public string MessageIdsJson { get; set; } = "[]";
    public bool ContextInvalidated { get; set; }
    public long Revision { get; set; }
    public string Text { get; set; } = "";
}

public sealed class GlobalEmotionState
{
    public Guid BotId { get; set; }
    public double Valence { get; set; }
    public double Arousal { get; set; } = .4;
    public double Energy { get; set; } = .7;
    public long Revision { get; set; } = 1;
    public DateTimeOffset LastEvolvedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EmotionEvent : Entity
{
    public Guid BotId { get; set; }
    public long Sequence { get; set; }
    public Guid SourceReflectionId { get; set; }
    public string SourceEventKey { get; set; } = "mood";
    public string DeltaJson { get; set; } = "{}";
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DailyNarrative : Entity
{
    public Guid BotId { get; set; }
    public DateOnly Date { get; set; }
    public long StateRevision { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RelationshipState : Entity
{
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public string? GroupId { get; set; }
    public string PreferencesJson { get; set; } = "[]";
    public string BoundariesJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
}

public sealed class RelationshipObservation : Entity
{
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public string? GroupId { get; set; }
    public Guid ReflectionId { get; set; }
    public string Candidate { get; set; } = "";
    public string EvidenceJson { get; set; } = "[]";
    public bool Accepted { get; set; }
}

public sealed class Memory : Entity
{
    public Guid? SharedFromMemoryId { get; set; }
    public Guid BotId { get; set; }
    public Guid SubjectId { get; set; }
    public MemoryScope Scope { get; set; }
    public string ScopeOwnerId { get; set; } = "";
    public string FactKey { get; set; } = "";
    public string Text { get; set; } = "";
    public MemoryStatus Status { get; set; } = MemoryStatus.PendingEmbedding;
    public Guid SourceReflectionId { get; set; }
    public string EvidenceJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemoryRevocation : Entity
{
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public MemoryScope Scope { get; set; }
    public string ScopeOwnerId { get; set; } = "";
    public string FactKeyHash { get; set; } = "";
    public long Epoch { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EmbeddingProfile : Entity
{
    public Guid BotId { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int Dimensions { get; set; } = 1536;
    public string Metric { get; set; } = "cosine";
    public int Version { get; set; } = 1;
}

public sealed class ReflectionResult : Entity
{
    public Guid BotId { get; set; }
    public Guid SessionId { get; set; }
    public Guid SubjectId { get; set; }
    public string? GroupId { get; set; }
    public string Kind { get; set; } = "full";
    public long Boundary { get; set; }
    public long RevocationEpoch { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string ModelId { get; set; } = "";
    public string Payload { get; set; } = "{}";
}

public sealed class OutboxMessage : Entity
{
    public long Sequence { get; set; }
    public Guid BotId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ErrorCode { get; set; }
}

public sealed class InboxProcessedEvent
{
    public Guid EventId { get; set; }
    public string HandlerName { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class OutgoingMessageBatch : Entity
{
    public Guid BotId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SessionId { get; set; }
    public Guid SourceJobId { get; set; }
}

public sealed class OutgoingMessagePart : Entity
{
    public Guid BatchId { get; set; }
    public int Sequence { get; set; }
    public string Text { get; set; } = "";
    public DeliveryStatus Status { get; set; }
    public string? ExternalId { get; set; }
}

public sealed class ModelProfile : Entity
{
    public Guid BotId { get; set; }
    public TaskKind TaskKind { get; set; }
    public string Provider { get; set; } = "openai-compatible";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public int ContextWindow { get; set; } = 8192;
    public int MaxOutputTokens { get; set; } = 1024;
    public bool SupportsImages { get; set; }
    public bool SupportsJson { get; set; }
    public bool SupportsTools { get; set; }
    public bool? ThinkingEnabled { get; set; }
    public string? ReasoningEffort { get; set; }
    public int EmbeddingDimensions { get; set; } = 1536;
    public decimal InputCostPerMillion { get; set; }
    public decimal OutputCostPerMillion { get; set; }
}

public sealed class UsageRecord : Entity
{
    public Guid BotId { get; set; }
    public Guid ModelProfileId { get; set; }
    public TaskKind TaskKind { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal Cost { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MediaAsset : Entity
{
    public Guid BotId { get; set; }
    public Guid MessageId { get; set; }
    public string Path { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}