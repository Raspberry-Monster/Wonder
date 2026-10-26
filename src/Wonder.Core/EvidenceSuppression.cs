namespace Wonder.Core;

public sealed record EvidenceSpan(int Start, int Length);

public sealed record SuppressedEvidence(
    Guid MemoryId,
    Guid MessageId,
    string Version,
    string FactKeyHash,
    EvidenceSpan[]? Spans = null);