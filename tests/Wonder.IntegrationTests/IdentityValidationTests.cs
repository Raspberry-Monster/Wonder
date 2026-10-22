using Wonder.Host;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class IdentityValidationTests
{
    [Test]
    public async Task IdentityLocksAfterFiveFailuresAndResetsAfterExpiry()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "locked@example.com", password = "A-valid-password!" };
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/register", credentials)).StatusCode)
            .IsEqualTo(HttpStatusCode.Created);
        for (var i = 0; i < 5; i++)
            await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/login",
                new { credentials.email, password = "The-wrong-password!" })).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/login", credentials)).StatusCode)
            .IsEqualTo(HttpStatusCode.Unauthorized);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var user = await db.Users.SingleAsync();
        await Assert.That(user.LockoutEnabled).IsTrue();
        await Assert.That(user.LockoutEnd > DateTimeOffset.UtcNow).IsTrue();
        user.LockoutEnd = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "LOCKED@EXAMPLE.COM", credentials.password })).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await db.Entry(user).ReloadAsync();
        await Assert.That(user.AccessFailedCount).IsEqualTo(0);
        await Assert.That((await client.PostAsync("/api/v1/auth/logout", null)).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await client.GetAsync("/api/v1/me")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task DuplicateNormalizedEmailConflictsAndMalformedInputHasFieldErrors()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "duplicate@example.com", password = "A-valid-password!" };
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/register", credentials)).StatusCode)
            .IsEqualTo(HttpStatusCode.Created);
        await Assert.That((await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email = " DUPLICATE@EXAMPLE.COM ", credentials.password })).StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        var invalid = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "invalid", password = "short" });
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(problem.GetProperty("errors").TryGetProperty("Email", out _)).IsTrue();
        await Assert.That(problem.GetProperty("errors").TryGetProperty("Password", out _)).IsTrue();
    }

    [Test]
    public async Task FluentValidationRejectsInvalidBotAndNullOrInvalidModelRoutesBeforeMutation()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Wonder", "1");
        var credentials = new { email = "validation@example.com", password = "A-valid-password!" };
        await client.PostAsJsonAsync("/api/v1/auth/register", credentials);
        await client.PostAsJsonAsync("/api/v1/auth/login", credentials);
        await Assert.That((await client.PostAsJsonAsync("/api/v1/bots", new { name = "  ", identity = "ok" })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        var created = await client.PostAsJsonAsync("/api/v1/bots", new { name = "Valid", identity = "Identity" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Assert.That((await client.PatchAsJsonAsync($"/api/v1/bots/{id}",
            new { privateIdleMinutes = 0, name = "  " })).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PutAsJsonAsync($"/api/v1/bots/{id}/model-routes", new object?[] { null })).StatusCode)
            .IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await client.PutAsJsonAsync($"/api/v1/bots/{id}/model-routes", new object[]
        {
            new { taskKind = "Chat", endpoint = "https://example.com/v1", model = "valid", apiKey = "secret" },
            new { taskKind = "Embedding", endpoint = "file:///invalid", model = "invalid" }
        })).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Models.CountAsync()).IsEqualTo(0);
        await Assert.That((await db.Bots.SingleAsync()).PrivateIdleMinutes).IsEqualTo(10);
    }
}
