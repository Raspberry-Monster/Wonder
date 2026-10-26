namespace Wonder.Core;

// A message uses a content fingerprint; mutable versioned resources use their revision.
// These describe actual consumed resources, never inferred sentence-level attribution.
public sealed record ContentSource(string Kind, Guid Id, string Version);
public sealed record SafeSummaryProjection(string Text, IReadOnlyList<ContentSource> Sources);

// HTTP idempotency is durable and independent of Outbox delivery/lease state.
// No copy of the edited body is kept in the receipt.
public sealed class MemoryOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ActorId { get; set; }
    public Guid BotId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string ArgumentsHash { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid TargetMemoryId { get; set; }
    public long ExpectedRevision { get; set; }
    public Guid ResultMemoryId { get; set; }
    public long ResultRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Stage { get; set; } = "Completed";
    public string ScopeHash { get; set; } = "";
    public string TargetsJson { get; set; } = "[]";
    public string AssertionHashesJson { get; set; } = "[]";
    public DateTimeOffset? PreviewExpiresAt { get; set; }
    public bool Fenced { get; set; }
    public bool CleanupVerified { get; set; }
    public long Revision { get; set; } = 1;
    public string? ErrorCode { get; set; }
}

public sealed record MemoryOperationReceipt(Guid OperationId, string Kind, Guid TargetMemoryId,
    Guid ResultMemoryId, long Revision, string FactState);

public sealed record MemoryView(Guid Id, Guid BotId, string FactKey, string Text, MemoryStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset? ArchivedAt, Guid? SupersedesMemoryId,
    DateTimeOffset? LastConfirmedAt, long Revision, bool IsOutdated);
public sealed record MemoryCapabilities(bool Edit, bool Archive, bool Forget, bool Erase);
public sealed record MemoryIndexState(long Revision, string State, int Attempts);

public sealed record PrivacyTarget(string Kind, Guid Id, string Version, string? MediaLocator = null, string? ContentHash = null);
public sealed class MemoryOperationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public string Kind { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string SourceVersion { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset? VerifiedAt { get; set; }
}

// Suppression binds an old assertion to its actual evidence, never to an everlasting semantic ban.
public sealed class EvidenceSuppression
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OperationId { get; set; }
    public Guid BotId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid MemoryId { get; set; }
    public Guid MessageId { get; set; }
    public string MessageVersion { get; set; } = "";
    public string SpansJson { get; set; } = "[]";
    public string FactKeyHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record EvidenceAssertion(Guid MessageId, string Role, int Start, int Length,
    string Anchor, int? ConfirmationStart = null, string? ConfirmationAnchor = null);

public sealed class RelationshipClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RelationshipId { get; set; }
    public Guid BotId { get; set; }
    public Guid SubjectId { get; set; }
    public string Kind { get; set; } = "Preference";
    public string Text { get; set; } = "";
    public string Origin { get; set; } = "Unknown";
    public string Qualification { get; set; } = "Unverified";
    public string EvidenceJson { get; set; } = "[]";
    public string MemorySourcesJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
}

public sealed record PrivacyItemView(Guid Id, string Kind, Guid ResourceId, string State, string? ErrorCode);
public sealed record PrivacyOperationView(Guid OperationId, string Kind, string Stage, long Revision,
    bool Fenced, bool CleanupVerified, bool Completed,
    IReadOnlyList<PrivacyItemView> Items, string[] UncoveredLayers, string? ErrorCode, Guid TargetMemoryId, long TargetRevision);
public sealed record ErasePreviewView(Guid PreviewId, DateTimeOffset ExpiresAt, string ScopeHash,
    long ExpectedRevision, IReadOnlyList<EraseMessageView> Messages, IReadOnlyList<PrivacyTarget> Targets,
    string Warning, string[] UncoveredLayers);
public sealed record EraseMessageView(Guid Id, Guid BotId, Guid AuthorId, DateTimeOffset SentAt, string Text,
    string Version);
