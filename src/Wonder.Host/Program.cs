using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Wonder.Chat;
using Wonder.Emotion;
using Wonder.Host;
using Wonder.Infrastructure;
using Wonder.Persona;
using Wonder.Platforms;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddConsole();
builder.Configuration.AddJsonFile("appsettings.Local.json", true).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddDbContextFactory<WonderDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres") ??
                throw new InvalidOperationException("Postgres connection string required.")));
builder.Services.AddDataProtection().SetApplicationName("Wonder")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Storage:KeysPath"] ?? "data/keys"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "wonder.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c =>
    {
        c.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = c =>
    {
        c.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth",
        context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
            _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddHttpClient("models");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(new ConfigurationOptions
{
    EndPoints = { builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379" },
    AbortOnConnectFail = false,
    ConnectTimeout = 3000,
    AsyncTimeout = 3000,
    Password = builder.Configuration["Redis:Password"]
}));
builder.Services.AddSingleton<PromptComposer>();
builder.Services.AddSingleton<ContextBuilder>();
builder.Services.AddSingleton<EmotionEngine>();
builder.Services.AddSingleton<MessageIngestor>();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddScoped<ChatWorkflow>();
builder.Services.AddScoped<OutgoingWorkflow>();
builder.Services.AddScoped<ReflectionWorkflow>();
builder.Services.AddScoped<MaintenanceWorkflow>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<OneBotConnectionManager>();
builder.Services.AddSingleton<IPlatformConnection>(sp => sp.GetRequiredService<OneBotConnectionManager>());
if (builder.Configuration.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OneBotConnectionManager>());
    builder.Services.AddHostedService<JobWorkers>();
}

var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<WonderDbContext>().Database.MigrateAsync();
    return;
}

if (Array.IndexOf(args, "--replay-ledger") is var ledgerIndex && ledgerIndex >= 0)
{
    if (ledgerIndex + 1 >= args.Length) throw new ArgumentException("Supply a decrypted deletion-ledger file.");
    await app.Services.GetRequiredService<MemoryService>()
        .ReplayLedgerAsync(args[ledgerIndex + 1], CancellationToken.None);
    return;
}

app.UseExceptionHandler(error => error.Run(async c =>
{
    var ex = c.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = ex switch
    {
        KeyNotFoundException => 404, DbUpdateConcurrencyException => 409, DbUpdateException => 409,
        ArgumentException => 400, _ => 500
    };
    await Results.Problem(statusCode: status,
        title: status switch
        {
            404 => "Resource not found", 409 => "State changed; reload and retry", 400 => "Invalid request",
            _ => "Request failed"
        }).ExecuteAsync(c);
}));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") &&
        context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && context.Request.Headers["X-Wonder"] != "1")
    {
        context.Response.StatusCode = 400;
        return;
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (WonderDbContext db, IConnectionMultiplexer redis, CancellationToken ct) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(ct)) return Results.StatusCode(503);
        await redis.GetDatabase().PingAsync();
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception)
    {
        return Results.StatusCode(503);
    }
});
app.MapOpenApi().RequireAuthorization();
app.MapControllers();
await app.RunAsync();

public partial class Program;