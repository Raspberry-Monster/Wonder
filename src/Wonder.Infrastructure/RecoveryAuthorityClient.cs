using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Wonder.Core;

namespace Wonder.Infrastructure;

// Wire adapter for an operator-provided, independent durable authority. Nothing is provisioned or trusted by default.
public sealed class RecoveryAuthorityClient(IHttpClientFactory clients, IConfiguration configuration) : IRecoveryAuthority
{
    public Guid InstanceId => Guid.TryParse(configuration["Recovery:InstanceId"], out var id) ? id : Guid.Empty;
    public bool Configured => InstanceId != Guid.Empty &&
        Uri.TryCreate(configuration["Recovery:AuthorityUrl"], UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        !string.IsNullOrWhiteSpace(configuration["Recovery:PinnedPublicKeyPem"]) &&
        !string.IsNullOrWhiteSpace(configuration["Recovery:TrustedBaseline"]);

    public Task<ProtectionReceipt> PrepareAsync(ProtectionIntent intent, CancellationToken ct) => SendIntentAsync("Prepare", intent, null, ct);
    public Task<ProtectionReceipt> ResolveAsync(ProtectionIntent intent, bool committed, CancellationToken ct) =>
        SendIntentAsync(committed ? "Commit" : "Abort", intent, null, ct);
    public Task<ProtectionReceipt> ProtectAsync(ProtectionIntent intent, RecoveryArchive archive, CancellationToken ct) => SendIntentAsync("Protect", intent, archive, ct);

    private async Task<ProtectionReceipt> SendIntentAsync(string phase, ProtectionIntent intent, RecoveryArchive? archive, CancellationToken ct)
    {
        if (intent.InstanceId != InstanceId) throw new RecoveryUnavailableException("RecoveryInstanceMismatch");
        var hash = Json.Hash(Json.Write(intent));
        // The durable Protect acknowledgment covers the actual bytes and manifest, not just the intent.
        var manifestDigest = archive is null ? null : Json.Hash(archive.ManifestJson);
        var exchangeHash = archive is null ? hash : Json.Hash(Json.Write(new { intentHash = hash, archiveDigest = archive.Digest, manifestDigest }));
        var response = await ExchangeAsync(phase, intent.OperationId, exchangeHash, new { intent, archive }, ct);
        return new ProtectionReceipt(intent.OperationId, hash, phase, response.Sequence, Json.Write(response), archive?.Digest, manifestDigest);
    }

    public async Task<RecoveryProof> VerifyAsync(RecoveryCoverage coverage, CancellationToken ct)
    {
        if (coverage.InstanceId != InstanceId || coverage.Baseline != configuration["Recovery:TrustedBaseline"])
            throw new RecoveryUnavailableException("RecoveryBaselineMismatch");
        var signed = await ExchangeAsync("Verify", Guid.Empty, Json.Hash(Json.Write(coverage)), coverage, ct);
        if (signed.PendingIntents != 0 || !signed.Durable) throw new RecoveryUnavailableException("UnresolvedRecoveryIntent");
        return new RecoveryProof(true, "Passed", Json.Write(signed));
    }

    private async Task<SignedAuthorityReply> ExchangeAsync(string phase, Guid operation, string hash, object payload, CancellationToken ct)
    {
        if (!Configured) throw new RecoveryUnavailableException("IndependentRecoveryAuthorityNotConfigured");
        var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var client = clients.CreateClient("recovery-authority");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(configuration["Recovery:AuthorityUrl"]!), "v1/protection"));
        request.Content = JsonContent.Create(new { phase, instanceId = InstanceId, operationId = operation,
            intentHash = hash, challenge, baseline = configuration["Recovery:TrustedBaseline"], payload }, options: Json.Options);
        using var response = await client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new RecoveryUnavailableException("RecoveryAuthorityRejected");
        var reply = await response.Content.ReadFromJsonAsync<SignedAuthorityReply>(Json.Options, timeout.Token)
                    ?? throw new RecoveryUnavailableException("MissingRecoveryReceipt");
        if (reply.InstanceId != InstanceId || reply.OperationId != operation || reply.Phase != phase ||
            reply.IntentHash != hash || reply.Challenge != challenge || reply.Sequence < 0 || !reply.Durable)
            throw new RecoveryUnavailableException("RecoveryReceiptScopeMismatch");
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(configuration["Recovery:PinnedPublicKeyPem"]!);
            if (!key.VerifyData(System.Text.Encoding.UTF8.GetBytes(Json.Write(reply with { Signature = "" })),
                    Convert.FromBase64String(reply.Signature), HashAlgorithmName.SHA256))
                throw new RecoveryUnavailableException("RecoveryReceiptSignatureInvalid");
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        { throw new RecoveryUnavailableException("RecoveryTrustKeyInvalid"); }
        return reply;
    }
}

public sealed record SignedAuthorityReply(Guid InstanceId, Guid OperationId, string Phase, string IntentHash,
    string Challenge, long Sequence, bool Durable, int PendingIntents, string Signature);
