using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class RecoveryBoundaryTests
{
    [Test]
    [Arguments("MissingItem")]
    [Arguments("ItemVersion")]
    [Arguments("UnknownKind")]
    [Arguments("SummaryFence")]
    [Arguments("SummaryPayload")]
    [Arguments("ProjectionPayload")]
    public async Task RecoveryRejectsIncompleteManifestAndRestoredSummaryPayloads(string fault)
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new PrivacyTests.TestAuthority();
        using var app = PrivacyTests.App(pg, authority); var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope(); var services = scope.ServiceProvider; var db = services.GetRequiredService<WonderDbContext>();
        var summary = await db.Summaries.SingleAsync(); var memory = await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.Old);
        summary.SafeProjectionJson = Json.Write(new SafeSummaryProjection("test projection", [ContentSources.Memory(memory)]));
        summary.ProjectionRevision = 1; summary.ProjectionBaseSummaryRevision = summary.Revision;
        summary.ProjectionState = "Ready"; summary.ProjectionExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await db.SaveChangesAsync();
        var privacy = services.GetRequiredService<PrivacyWorkflow>();
        var preview = await privacy.PreviewAsync(f.User, memory.Id, 1, [f.Evidence], default);
        var result = await privacy.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "erase", default);
        await Assert.That(result.Completed).IsTrue();
        var bootstrap = new RecoveryBootstrap(services.GetRequiredService<IDbContextFactory<WonderDbContext>>(), authority,
            services.GetRequiredService<RecoveryPackages>(), services.GetRequiredService<IConfiguration>());
        await Assert.That((await bootstrap.VerifyAsync(default)).Passed).IsTrue();
        if (fault == "MissingItem") await db.MemoryOperationItems.Where(x => x.OperationId == result.OperationId && x.Kind == "Summary").ExecuteDeleteAsync();
        if (fault == "ItemVersion") await db.MemoryOperationItems.Where(x => x.OperationId == result.OperationId && x.Kind == "Summary")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceVersion, "999"));
        if (fault == "UnknownKind") await db.MemoryOperationItems.Where(x => x.OperationId == result.OperationId && x.Kind == "Summary")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, "Unknown"));
        if (fault is "SummaryFence" or "SummaryPayload") await db.Summaries.Where(x => x.Id == summary.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "restored private summary").SetProperty(x => x.Revision, 1)
                .SetProperty(x => x.ContextInvalidated, fault != "SummaryFence"));
        if (fault == "ProjectionPayload") await db.Summaries.Where(x => x.Id == summary.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.SafeProjectionJson, Json.Write(new SafeSummaryProjection("restored projection", [ContentSources.Memory(memory)])))
            .SetProperty(x => x.ProjectionState, "Ready").SetProperty(x => x.ProjectionRevision, 99)
            .SetProperty(x => x.ProjectionBaseSummaryRevision, x => x.Revision).SetProperty(x => x.ProjectionExpiresAt, DateTimeOffset.UtcNow.AddHours(1)));
        await Assert.That(async () => { await bootstrap.VerifyAsync(default); }).ThrowsExactly<RecoveryUnavailableException>();
    }

    [Test]
    public async Task StartupReverifiesActualPayloadsInsteadOfTrustingCompletedFlags()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new PrivacyTests.TestAuthority();
        using var app = PrivacyTests.App(pg, authority); var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope(); var services = scope.ServiceProvider; var db = services.GetRequiredService<WonderDbContext>();
        await services.GetRequiredService<PrivacyWorkflow>().ForgetAsync(f.User, f.Old!.Value, 1, "complete", default);
        var bootstrap = new RecoveryBootstrap(services.GetRequiredService<IDbContextFactory<WonderDbContext>>(), authority,
            services.GetRequiredService<RecoveryPackages>(), services.GetRequiredService<IConfiguration>());
        await Assert.That((await bootstrap.VerifyAsync(default)).Passed).IsTrue();
        await db.MemoryCandidates.ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "restored old payload"));
        await Assert.That(async () => { await bootstrap.VerifyAsync(default); }).ThrowsExactly<RecoveryUnavailableException>();
        await db.MemoryCandidates.ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, ""));
        db.Revocations.Add(new MemoryRevocation { BotId = f.Bot, UserId = f.User, ResourceKind = DeletionResourceKind.MemoryContext });
        await db.SaveChangesAsync();
        await Assert.That(async () => { await bootstrap.VerifyAsync(default); }).ThrowsExactly<RecoveryUnavailableException>();
    }
    [Test]
    public async Task SignedProtocolBindsNonceScopePackageAndLatestCoverage()
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var instance = Guid.NewGuid(); var handler = new AuthorityHandler(signer); var client = new RecoveryAuthorityClient(handler,
            Config(new() { ["Recovery:InstanceId"] = instance.ToString(), ["Recovery:AuthorityUrl"] = "https://isolated.invalid/",
                ["Recovery:PinnedPublicKeyPem"] = signer.ExportSubjectPublicKeyInfoPem(), ["Recovery:TrustedBaseline"] = "test-only" }));
        var intent = new ProtectionIntent(Guid.NewGuid(), instance, Guid.NewGuid(), Guid.NewGuid(), "Forget", new string('A', 64),
            [new PrivacyTarget("Memory", Guid.NewGuid(), "1")], []);
        var receipt = await client.PrepareAsync(intent, default);
        await Assert.That(receipt.IntentHash).IsEqualTo(Json.Hash(Json.Write(intent)));
        handler.Replay = true;
        await Assert.That(async () => { await client.PrepareAsync(intent, default); }).ThrowsExactly<RecoveryUnavailableException>();
        handler.Replay = false; handler.WrongScope = true;
        await Assert.That(async () => { await client.PrepareAsync(intent, default); }).ThrowsExactly<RecoveryUnavailableException>();
        handler.WrongScope = false; handler.BadSignature = true;
        await Assert.That(async () => { await client.PrepareAsync(intent, default); }).ThrowsExactly<RecoveryUnavailableException>();
        handler.BadSignature = false;
        var archive = new RecoveryArchive([1, 2, 3], "manifest-only", "archive-digest");
        var protectedReceipt = await client.ProtectAsync(intent, archive, default);
        await Assert.That(protectedReceipt.ArchiveDigest).IsEqualTo(archive.Digest);
        await Assert.That(handler.LastHash).IsEqualTo(Json.Hash(Json.Write(new { intentHash = Json.Hash(Json.Write(intent)),
            archiveDigest = archive.Digest, manifestDigest = Json.Hash(archive.ManifestJson) })));
        handler.Pending = 1;
        await Assert.That(async () => { await client.VerifyAsync(new RecoveryCoverage(instance, "schema", "coverage", "test-only"), default); }).ThrowsExactly<RecoveryUnavailableException>();
        handler.Pending = 0;
        var proof = await client.VerifyAsync(new RecoveryCoverage(instance, "schema", "coverage", "test-only"), default);
        await Assert.That(proof.Passed).IsTrue();
        handler.Replay = true;
        await Assert.That(async () => { await client.VerifyAsync(new RecoveryCoverage(instance, "schema", "older-coverage", "test-only"), default); }).ThrowsExactly<RecoveryUnavailableException>();
    }

    [Test]
    public async Task InvalidArchiveKeyIsUnavailableBeforeAnyPrivacyAdmission()
    {
        var packages = new RecoveryPackages(Config(new() { ["Recovery:ArchiveKeyBase64"] = "not-a-key",
            ["Recovery:ArchiveKeyId"] = "test", ["Recovery:TrustedBaseline"] = "test" }));
        await Assert.That(packages.Configured).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedRecoveryStartsOnlyReadOnlyAuthenticatedDiagnosticsOrExits(bool diagnosticWeb)
    {
        var root = FindRoot(); var directory = Path.Combine(root, ".local", "recovery-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var businessPort = FreePort(); var diagnosticPort = FreePort();
        using var databaseProbe = new TcpListener(IPAddress.Loopback, 0); databaseProbe.Start();
        var databasePort = ((IPEndPoint)databaseProbe.LocalEndpoint).Port;
        using var probeCancel = new CancellationTokenSource(); var connected = databaseProbe.AcceptTcpClientAsync(probeCancel.Token).AsTask();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true };
        var buildConfiguration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        start.ArgumentList.Add(Path.Combine(root, "src/Wonder.Host/bin", buildConfiguration, "net10.0/Wonder.Host.dll"));
        start.ArgumentList.Add("--contentRoot"); start.ArgumentList.Add(directory);
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:" + businessPort);
        start.Environment["ConnectionStrings__Postgres"] = $"Host=127.0.0.1;Port={databasePort};Database=unused;Username=unused";
        start.Environment["Workers__Enabled"] = "true";
        start.Environment["Recovery__AuthorityUrl"] = "";
        start.Environment["Recovery__InstanceId"] = "";
        start.Environment["Recovery__DiagnosticPort"] = diagnosticPort.ToString();
        start.Environment["Recovery__DiagnosticTokenHash"] = diagnosticWeb ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) : "bad-hash";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (diagnosticWeb)
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + diagnosticPort), Timeout = TimeSpan.FromSeconds(3) };
                HttpResponseMessage? ready = null;
                for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
                {
                    try { ready = await http.GetAsync("/"); if (ready.IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(50);
                }
                await Assert.That(ready?.IsSuccessStatusCode == true).IsTrue();
                var generic = await ready!.Content.ReadAsStringAsync();
                await Assert.That(generic).Contains("RecoveryBlocked"); await Assert.That(generic).DoesNotContain("stackTrace");
                await Assert.That((await http.GetAsync("/diagnostics")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                http.DefaultRequestHeaders.Authorization = new("Bearer", token);
                var details = await http.GetFromJsonAsync<RecoveryDiagnostic>("/diagnostics", Json.Options);
                await Assert.That(details!.Exception.Message).IsEqualTo("IndependentRecoveryProtectionNotConfigured");
                await Assert.That(details.Exception.StackTrace.Length > 0).IsTrue();
                await Assert.That(Json.Write(details)).DoesNotContain(token);
                foreach (var path in new[] { "/api/v1/me", "/health/live", "/openapi/v1.json", "/me/memories" })
                    await Assert.That((await http.GetAsync(path)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That((await http.PostAsync("/diagnostics", null)).StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
                using var business = new TcpListener(IPAddress.Loopback, businessPort);
                business.Server.ExclusiveAddressUse = true;
                business.Start(); // Prove the ordinary business socket was never bound by the child.
                business.Stop();
            }
            else
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.That(process.ExitCode).IsEqualTo(78);
                await Assert.That(await errors).Contains("RecoveryVerification");
            }
            await Assert.That(connected.IsCompleted).IsFalse();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            probeCancel.Cancel(); databaseProbe.Stop();
            await File.WriteAllTextAsync(Path.Combine(directory, "diagnostic-redacted.log"), await output + await errors);
        }
    }

    [Test]
    public async Task DiagnosticKeepsRealExceptionChainFramesAndRedactsFreeFormSecrets()
    {
        Exception captured;
        try { try { throw new IOException("secret connection and user body"); } catch (IOException inner) { throw new InvalidOperationException("private path", inner); } }
        catch (Exception ex) { captured = ex; }
        var diagnostic = RecoveryDiagnostics.Capture(captured, "Test");
        var json = Json.Write(diagnostic);
        await Assert.That(json).DoesNotContain("secret connection"); await Assert.That(json).DoesNotContain("private path");
        await Assert.That(diagnostic.Exception.Inner!.Type).IsEqualTo(typeof(IOException).FullName);
        await Assert.That(diagnostic.Exception.StackTrace.Length > 0 && diagnostic.Exception.Inner.StackTrace.Length > 0).IsTrue();
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static string FindRoot()
    { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(directory.FullName, "Wonder.slnx"))) directory = directory.Parent!; return directory.FullName; }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }

    private sealed class AuthorityHandler(ECDsa key) : HttpMessageHandler, IHttpClientFactory
    {
        public bool Replay { get; set; } public bool WrongScope { get; set; } public bool BadSignature { get; set; } public int Pending { get; set; }
        public string? LastHash { get; private set; } private SignedAuthorityReply? last;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var node = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            LastHash = node["intentHash"]!.GetValue<string>();
            var reply = new SignedAuthorityReply(Guid.Parse(node["instanceId"]!.GetValue<string>()), Guid.Parse(node["operationId"]!.GetValue<string>()),
                node["phase"]!.GetValue<string>(), WrongScope ? "wrong" : LastHash, node["challenge"]!.GetValue<string>(), 1, true, Pending, "");
            reply = reply with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Json.Write(reply)), HashAlgorithmName.SHA256)) };
            if (BadSignature) reply = reply with { Signature = Convert.ToBase64String(new byte[64]) };
            if (Replay) reply = last!; else last = reply;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply, options: Json.Options) };
        }
    }
}
