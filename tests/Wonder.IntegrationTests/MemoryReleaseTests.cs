using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class MemoryReleaseTests
{
    // Real PG/bootstrap/HTTP, simulated independent authority and model/QQ only. Not S-04 certification.
    private static PrivacyTests.TestAuthority Authority(string? receipt = "test-only-authority-receipt") =>
        new() { StartupProof = new RecoveryProof(true, "Passed", receipt) };

    [Test]
    [Arguments(false, true, "Disabled")]
    [Arguments(true, false, "AwaitingRecoveryVerification")]
    public async Task ConfigurationAloneOrAnUnreleasedBuildCannotPartiallyOpenOperations(bool enabled, bool verify, string state)
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = Authority();
        using var app = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString,
            recoveryAuthority: authority, lifecycleEnabled: enabled, realRecoveryStartup: verify);
        var f = await PrivacyTests.SeedAsync(app); using var client = await LoginAsync(app, f.User);
        await AssertClosedAsync(client, f.Old!.Value);
        await Assert.That((await client.GetFromJsonAsync<MemoryReleaseStatus>("/api/v1/me/memory-release-status"))!.State).IsEqualTo(state);
    }

    [Test]
    public async Task AProofWithoutItsVerifiedReceiptCannotEnableRelease()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = Authority(null);
        using var app = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString,
            recoveryAuthority: authority, lifecycleEnabled: true, realRecoveryStartup: true);
        var f = await PrivacyTests.SeedAsync(app); using var client = await LoginAsync(app, f.User);
        await AssertClosedAsync(client, f.Old!.Value);
    }

    [Test]
    public async Task ProductionReleasePointEnablesAllFourAndClosingItDisablesAllFour()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = Authority();
        using var app = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString,
            recoveryAuthority: authority, lifecycleEnabled: true, realRecoveryStartup: true);
        var f = await PrivacyTests.SeedAsync(app); using var client = await LoginAsync(app, f.User);
        var capabilities = await client.GetFromJsonAsync<MemoryCapabilities>("/api/v1/me/memory-capabilities");
        await Assert.That(capabilities).IsEqualTo(new MemoryCapabilities(true, true, true, true));
        await Assert.That((await client.GetFromJsonAsync<MemoryReleaseStatus>("/api/v1/me/memory-release-status"))!.State).IsEqualTo("Ready");
        var root = "/api/v1/me/memories/" + f.Old;
        using var edit = await SendAsync(client, HttpMethod.Patch, root, new { text = "corrected current fact", expectedRevision = 1 }, "edit");
        await Assert.That(edit.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var corrected = (await edit.Content.ReadFromJsonAsync<MemoryOperationReceipt>())!;
        using var archive = await SendAsync(client, HttpMethod.Post, "/api/v1/me/memories/" + corrected.ResultMemoryId + "/archive",
            new { expectedRevision = corrected.Revision }, "archive");
        await Assert.That(archive.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var forget = await SendAsync(client, HttpMethod.Delete, root + "?expectedRevision=2", null, "forget");
        await Assert.That(forget.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var forgotten = (await forget.Content.ReadFromJsonAsync<PrivacyOperationView>())!;
        using var preview = await client.PostAsJsonAsync(root + "/erase-preview", new { expectedRevision = forgotten.TargetRevision, messageIds = new[] { f.Evidence } });
        await Assert.That(preview.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var scope = (await preview.Content.ReadFromJsonAsync<ErasePreviewView>())!;
        using var erase = await SendAsync(client, HttpMethod.Post, root + "/erase", new { scope.PreviewId, scope.ExpectedRevision, scope.ScopeHash }, "erase");
        await Assert.That(erase.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await erase.Content.ReadFromJsonAsync<PrivacyOperationView>())!.Completed).IsTrue();
        app.Services.GetRequiredService<IConfiguration>()["MemoryLifecycle:Enabled"] = "false";
        await AssertClosedAsync(client, f.Old!.Value);
    }

    [Test]
    public async Task RecoveryConfigurationChangeInvalidatesTheReleaseProofForEveryOperation()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = Authority();
        using var app = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString,
            recoveryAuthority: authority, lifecycleEnabled: true, realRecoveryStartup: true);
        var f = await PrivacyTests.SeedAsync(app); using var client = await LoginAsync(app, f.User);
        await Assert.That((await client.GetFromJsonAsync<MemoryCapabilities>("/api/v1/me/memory-capabilities"))!.Forget).IsTrue();
        app.Services.GetRequiredService<IConfiguration>()["Recovery:ArchiveKeyId"] = "unverified-key-rotation";
        await AssertClosedAsync(client, f.Old!.Value);
        await Assert.That((await client.GetFromJsonAsync<MemoryReleaseStatus>("/api/v1/me/memory-release-status"))!.State).IsEqualTo("AwaitingRecoveryVerification");
    }

    private static async Task AssertClosedAsync(HttpClient client, Guid id)
    {
        await Assert.That(await client.GetFromJsonAsync<MemoryCapabilities>("/api/v1/me/memory-capabilities"))
            .IsEqualTo(new MemoryCapabilities(false, false, false, false));
        var root = "/api/v1/me/memories/" + id;
        using var edit = await SendAsync(client, HttpMethod.Patch, root, new { text = "must not write", expectedRevision = 1 }, "closed-edit");
        using var archive = await SendAsync(client, HttpMethod.Post, root + "/archive", new { expectedRevision = 1 }, "closed-archive");
        using var forget = await SendAsync(client, HttpMethod.Delete, root + "?expectedRevision=1", null, "closed-forget");
        using var erase = await SendAsync(client, HttpMethod.Post, root + "/erase", new { previewId = Guid.NewGuid(), expectedRevision = 1, scopeHash = new string('A', 64) }, "closed-erase");
        foreach (var response in new[] { edit, archive, forget, erase })
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<HttpClient> LoginAsync(TestApp app, Guid actor)
    {
        var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "release@example.test", password = "A-strong-test-password!" };
        await client.PostAsJsonAsync("/api/v1/auth/register", credentials); await client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        (await db.Users.SingleAsync(x => x.Email == credentials.email)).LogicalUserId = actor; await db.SaveChangesAsync();
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Idempotency-Key", key); if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}
