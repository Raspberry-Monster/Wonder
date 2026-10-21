using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wonder.Core;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.IntegrationTests;

public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly SqliteConnection? connection;
    private readonly TimeProvider? time;
    private readonly string environment;
    private readonly bool? requireHttps;
    private readonly string? postgresConnection;

    public TestApp(TimeProvider? time = null, string? databaseFile = null,
        string environment = "Development", bool? requireHttps = null, string? postgresConnection = null)
    {
        this.time = time;
        this.environment = environment;
        this.requireHttps = requireHttps;
        this.postgresConnection = postgresConnection;
        if (postgresConnection is null)
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = databaseFile ?? ":memory:", Pooling = false }.ToString());
            connection.Open();
        }
    }

    public List<string> ModelRequests { get; } = [];
    public Queue<string> ModelResponses { get; } = [];
    public Func<string, Task>? BeforeModelResponse { get; set; }
    public FakeSender Sender { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, c) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Workers:Enabled"] = "false", ["Storage:KeysPath"] = Path.GetFullPath(".local/test-keys"),
                ["Authentication:AllowRegistration"] = "true"
            };
            if (requireHttps.HasValue) settings["Authentication:RequireHttps"] = requireHttps.Value.ToString();
            c.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            if (time is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(time);
            }
            services.AddSingleton<IHttpClientFactory>(new FakeHttpFactory(ModelRequests, ModelResponses,
                request => BeforeModelResponse?.Invoke(request) ?? Task.CompletedTask));
            services.AddSingleton<IPlatformConnection>(Sender);
            services.RemoveAll<IDbContextFactory<WonderDbContext>>();
            services.RemoveAll<WonderDbContext>();
            services.RemoveAll<DbContextOptions<WonderDbContext>>();
            var optionsBuilder = new DbContextOptionsBuilder<WonderDbContext>();
            if (postgresConnection is null) optionsBuilder.UseSqlite(connection!);
            else optionsBuilder.UseNpgsql(postgresConnection, pg => pg.UseVector());
            var options = optionsBuilder.ConfigureWarnings(w => w.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning)).Options;
            if (postgresConnection is not null)
            {
                services.AddSingleton<IDbContextFactory<WonderDbContext>>(new PostgresFactory(options));
                services.AddScoped<WonderDbContext>(_ => new WonderDbContext(options));
                return;
            }
            services.AddSingleton<IDbContextFactory<WonderDbContext>>(new TestFactory(options));
            services.AddScoped<WonderDbContext>(_ => new TestDb(options));
            using var db = new TestDb(options);
            db.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection?.Dispose();
    }

    public sealed class FakeSender : IPlatformConnection
    {
        public List<string> Sent { get; } = [];
        public bool Unknown { get; set; }

        public Task<string> SendAsync(Guid bindingId, ConversationKind kind, string target, string text,
            CancellationToken ct)
        {
            if (Unknown) throw new DeliveryUnknownException("Simulated disconnect after sending.");
            Sent.Add(text);
            return Task.FromResult(Sent.Count.ToString());
        }
    }

    private sealed class FakeHttpFactory(List<string> requests, Queue<string> responses, Func<string, Task> beforeResponse) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new FakeHandler(requests, responses, beforeResponse));
        }
    }

    private sealed class FakeHandler(List<string> requests, Queue<string> responses, Func<string, Task> beforeResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            requests.Add(body);
            await beforeResponse(body);
            var payload = responses.Count > 0 ? responses.Dequeue() :
                "{\"choices\":[{\"message\":{\"content\":\"这是上一轮的回复。\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":8}}";
            // Real compatible APIs include the assistant role; the SDK deliberately does not invent it.
            if (JsonNode.Parse(payload) is JsonObject wire && wire["choices"] is JsonArray choices)
            {
                foreach (var choice in choices)
                    if (choice?["message"] is JsonObject message) message["role"] ??= "assistant";
                payload = wire.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    public sealed class TestFactory(DbContextOptions<WonderDbContext> options) : IDbContextFactory<WonderDbContext>
    {
        public WonderDbContext CreateDbContext()
        {
            return new TestDb(options);
        }
    }

    private sealed class PostgresFactory(DbContextOptions<WonderDbContext> options) : IDbContextFactory<WonderDbContext>
    {
        public WonderDbContext CreateDbContext() => new(options);
    }

    private sealed class TestDb(DbContextOptions<WonderDbContext> options) : WonderDbContext(options)
    {
        private static long sequence;

        protected override void OnModelCreating(ModelBuilder b)
        {
            base.OnModelCreating(b);
            b.Entity<ChatMessage>().Property(x => x.Sequence).ValueGeneratedNever();
            b.Entity<OutboxMessage>().Property(x => x.Sequence).ValueGeneratedNever();
            var vector = b.Entity<MemoryEmbedding>().Property(x => x.Values)
                .HasConversion(v => Json.Write(v.ToArray()), v => new Vector(Json.Read<float[]>(v))).HasColumnType("text");
            vector.Metadata.SetValueComparer(new ValueComparer<Vector>((a, b) => a!.ToArray().SequenceEqual(b!.ToArray()),
                a => a.ToArray().Aggregate(0, (x, y) => HashCode.Combine(x, y.GetHashCode())), a => new Vector(a.ToArray())));
            foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                    property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(x => x.UtcTicks,
                        x => new DateTimeOffset(x, TimeSpan.Zero)));
        }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            foreach (var entry in ChangeTracker.Entries<ChatMessage>().Where(x => x.State == EntityState.Added))
                entry.Entity.Sequence = Interlocked.Increment(ref sequence);
            foreach (var entry in ChangeTracker.Entries<OutboxMessage>().Where(x => x.State == EntityState.Added))
                entry.Entity.Sequence = Interlocked.Increment(ref sequence);
            return base.SaveChangesAsync(ct);
        }
    }
}
