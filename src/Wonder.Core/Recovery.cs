namespace Wonder.Core;

public sealed record EvidenceSpan(int Start, int Length);
public sealed record SuppressedEvidence(Guid MemoryId, Guid MessageId, string Version, string FactKeyHash, EvidenceSpan[]? Spans = null);
public sealed record ProtectionIntent(Guid OperationId, Guid InstanceId, Guid ActorId, Guid BotId,
    string Kind, string ScopeHash, PrivacyTarget[] Targets, SuppressedEvidence[] Suppressions);
public sealed record ProtectionReceipt(Guid OperationId, string IntentHash, string Phase,
    long Sequence, string ReceiptJson, string? ArchiveDigest = null, string? ManifestDigest = null);
public sealed record RecoveryArchive(byte[] Ciphertext, string ManifestJson, string Digest);
public sealed record RecoveryCoverage(Guid InstanceId, string Schema, string OperationsHash, string Baseline, string LegacyHash = "");
public sealed record RecoveryProof(bool Passed, string Code, string? Receipt = null);

// Implementations must prove remote durability and independent latestness. A local export is not this proof.
public interface IRecoveryAuthority
{
    bool Configured { get; }
    Guid InstanceId { get; }
    Task<ProtectionReceipt> PrepareAsync(ProtectionIntent intent, CancellationToken ct);
    Task<ProtectionReceipt> ResolveAsync(ProtectionIntent intent, bool committed, CancellationToken ct);
    Task<ProtectionReceipt> ProtectAsync(ProtectionIntent intent, RecoveryArchive archive, CancellationToken ct);
    Task<RecoveryProof> VerifyAsync(RecoveryCoverage coverage, CancellationToken ct);
}

public sealed class RecoveryUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
