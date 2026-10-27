using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class MilkySchemaTests
{
    [Test]
    public async Task PostgreSqlInitialSchemaStoresMilkyConnectionWithoutQqKeystore()
    {
        await using var pg = await TestPostgres.CreateAsync();
        await using var db = new WonderDbContext(new DbContextOptionsBuilder<WonderDbContext>()
            .UseNpgsql(pg.ConnectionString, p => p.UseVector()).Options);
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        await db.SaveChangesAsync();
        db.Bindings.Add(new PlatformBinding
        {
            BotId = bot.Id, PlatformAccountId = "123",
            ApiBaseUrl = "http://milky:3000", ProtectedAccessToken = "encrypted-token"
        });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        var binding = await db.Bindings.SingleAsync();
        await Assert.That(binding.AdapterType).IsEqualTo("milky");
        await Assert.That(binding.ApiBaseUrl).IsEqualTo("http://milky:3000");
        await Assert.That(binding.ProtectedAccessToken).IsEqualTo("encrypted-token");
        await Assert.That(await db.Database.SqlQueryRaw<int>("""
                                                             SELECT count(*)::int AS "Value" FROM information_schema.columns
                                                             WHERE table_schema = 'public' AND table_name = 'Bindings'
                                                               AND column_name IN ('WebSocketUrl', 'ProtectedKeystore')
                                                             """).SingleAsync()).IsEqualTo(0);
        var second = new BotInstance { OwnerId = owner.Id };
        db.Bots.Add(second);
        db.Bindings.Add(new PlatformBinding { BotId = second.Id, PlatformAccountId = "456" });
        await db.SaveChangesAsync();
        await Assert.That(await db.Bindings.CountAsync()).IsEqualTo(2);
        await Assert.That(db.Database.HasPendingModelChanges()).IsFalse();
    }
}