using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed record MemoryReleaseStatus(bool Enabled, bool Available, string State);

// One production release point for all four operations. Verification is set only by the startup path.
public sealed class MemoryLifecycleRelease(IConfiguration configuration, IHostEnvironment environment,
    IRecoveryAuthority authority, RecoveryPackages packages)
{
    private string? verifiedConfiguration;
    private bool InternalTesting => environment.IsEnvironment("MemoryLifecycleTest") &&
        configuration.GetValue<bool>("MemoryLifecycle:InternalTesting");
    private bool MaterialAvailable => authority.Configured && packages.Configured;
    private bool Enabled => configuration.GetValue<bool>("MemoryLifecycle:Enabled");
    private bool Released => Enabled && MaterialAvailable &&
        Volatile.Read(ref verifiedConfiguration) is { } verified && verified == ConfigurationFingerprint();

    public bool CanManage => Released || InternalTesting;
    public bool CanDelete => Released || InternalTesting && MaterialAvailable;

    internal string BeginStartupVerification() => ConfigurationFingerprint();

    internal void AcceptStartupProof(RecoveryProof proof, string expectedConfiguration)
    {
        // Test factories' IsolatedTestOnly proof cannot enable the production release point.
        Volatile.Write(ref verifiedConfiguration, proof.Passed && proof.Code == "Passed" &&
            !string.IsNullOrWhiteSpace(proof.Receipt) && MaterialAvailable && expectedConfiguration == ConfigurationFingerprint()
            ? expectedConfiguration : null);
    }

    public MemoryCapabilities Capabilities()
    {
        var released = Released;
        var manage = released || InternalTesting; var delete = released || InternalTesting && MaterialAvailable;
        return new(manage, manage, delete, delete);
    }
    public MemoryReleaseStatus Status()
    {
        var enabled = Enabled; var released = Released;
        return new(enabled, released, released ? "Ready" : InternalTesting ? "InternalTesting" :
            enabled ? "AwaitingRecoveryVerification" : "Disabled");
    }

    private string ConfigurationFingerprint() => Json.Hash(Json.Write(new
    {
        authority.InstanceId, baseline = configuration["Recovery:TrustedBaseline"],
        url = configuration["Recovery:AuthorityUrl"], publicKey = configuration["Recovery:PinnedPublicKeyPem"],
        keyId = configuration["Recovery:ArchiveKeyId"], keyHash = Json.Hash(configuration["Recovery:ArchiveKeyBase64"] ?? "")
    }));
}
