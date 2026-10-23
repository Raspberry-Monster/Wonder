using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

// Each test owns a disposable database; no application database is migrated or cleared.
internal sealed class TestPostgres(string adminConnection, string databaseName) : IAsyncDisposable
{
    public string ConnectionString { get; } = new NpgsqlConnectionStringBuilder(adminConnection)
        { Database = databaseName, Pooling = false }.ConnectionString;

    public static async Task<TestPostgres> CreateAsync()
    {
        var connection = Environment.GetEnvironmentVariable("WONDER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            Skip.Test("Set WONDER_TEST_POSTGRES for PostgreSQL / pgvector tests.");
        var name = "wonder_test_" + Guid.NewGuid().ToString("N");
        var database = new TestPostgres(connection!, name);
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = new WonderDbContext(new DbContextOptionsBuilder<WonderDbContext>()
                .UseNpgsql(database.ConnectionString, pg => pg.UseVector()).Options);
            await db.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public TestApp CreateApp()
    {
        return new TestApp(postgresConnection: ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
