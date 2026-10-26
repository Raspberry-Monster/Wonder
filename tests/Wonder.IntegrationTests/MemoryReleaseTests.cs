using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class MemoryReleaseTests
{
    [Test]
    public async Task ProductionWithoutRecoveryConfigurationAllowsAllFourWhileAutomaticLearningIsOff()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = new TestApp(environment: "Production", postgresConnection: pg.ConnectionString);
        var f = await PrivacyTests.SeedAsync(app);
        using var client = await LoginAsync(app, f.User);
        var capabilities = await client.GetFromJsonAsync<MemoryCapabilities>("/api/v1/me/memory-capabilities");
        await Assert.That(capabilities).IsEqualTo(new MemoryCapabilities(true, true, true, true));
        using (var settingsScope = app.Services.CreateScope())
        {
            await settingsScope.ServiceProvider.GetRequiredService<WonderDbContext>().LogicalUsers
                .Where(x => x.Id == f.User).ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowMemory, false));
        }

        await Assert.That((await client.GetAsync("/api/v1/me/memory-release-status")).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        var root = "/api/v1/me/memories/" + f.Old;
        using var edit = await SendAsync(client, HttpMethod.Patch, root,
            new { text = "corrected current fact", expectedRevision = 1 }, "edit");
        await Assert.That(edit.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var corrected = (await edit.Content.ReadFromJsonAsync<MemoryOperationReceipt>())!;
        using var archive = await SendAsync(client, HttpMethod.Post,
            "/api/v1/me/memories/" + corrected.ResultMemoryId + "/archive",
            new { expectedRevision = corrected.Revision }, "archive");
        await Assert.That(archive.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var forget = await SendAsync(client, HttpMethod.Delete, root + "?expectedRevision=2", null, "forget");
        await Assert.That(forget.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var forgotten = (await forget.Content.ReadFromJsonAsync<PrivacyOperationView>())!;
        using var preview = await client.PostAsJsonAsync(root + "/erase-preview",
            new { expectedRevision = forgotten.TargetRevision, messageIds = new[] { f.Evidence } });
        await Assert.That(preview.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var scope = (await preview.Content.ReadFromJsonAsync<ErasePreviewView>())!;
        using var erase = await SendAsync(client, HttpMethod.Post, root + "/erase",
            new { scope.PreviewId, scope.ExpectedRevision, scope.ScopeHash }, "erase");
        await Assert.That(erase.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await erase.Content.ReadFromJsonAsync<PrivacyOperationView>())!.Completed).IsTrue();
    }

    private static async Task<HttpClient> LoginAsync(TestApp app, Guid actor)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "release@example.test", password = "A-strong-test-password!" };
        await client.PostAsJsonAsync("/api/v1/auth/register", credentials);
        await client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        (await db.Users.SingleAsync(x => x.Email == credentials.email)).LogicalUserId = actor;
        await db.SaveChangesAsync();
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body,
        string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Idempotency-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}