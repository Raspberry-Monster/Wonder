using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Features.Chat;
using Wonder.Host.Features.Emotion;
using Wonder.Host.Features.Persona;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;
using Wonder.Platforms;

if (Array.IndexOf(args, "--convert-ledger") is var convertIndex && convertIndex >= 0)
{
    if (convertIndex + 2 >= args.Length) throw new ArgumentException("Supply input and a new output ledger path.");
    var converted = LegacyDeletionLedgerConverter.Convert(await File.ReadAllTextAsync(args[convertIndex + 1]));
    await using var target = new FileStream(args[convertIndex + 2], FileMode.CreateNew, FileAccess.Write);
    await using var writer = new StreamWriter(target);
    await writer.WriteAsync(converted);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var frontendRoot = Path.GetFullPath("../Wonder.Web/dist", builder.Environment.ContentRootPath);
if (Directory.Exists(frontendRoot))
{
    builder.Environment.WebRootPath = frontendRoot;
    builder.Environment.WebRootFileProvider = new PhysicalFileProvider(frontendRoot);
}

builder.Logging.ClearProviders().AddConsole();
builder.Configuration.AddJsonFile("appsettings.Local.json", true).AddEnvironmentVariables();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1024 * 1024);
builder.Services.AddProblemDetails();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddControllers(o => o.Filters.Add<RequestValidationFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddDbContextFactory<WonderDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres") ??
                throw new InvalidOperationException("Postgres connection string required."), pg => pg.UseVector()));
builder.Services.AddDataProtection().SetApplicationName("Wonder")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Storage:KeysPath"] ?? "data/keys"));
builder.Services.AddIdentityCore<ApplicationUser>(o =>
{
    o.User.RequireUniqueEmail = true;
    o.User.AllowedUserNameCharacters = "";
    o.Password.RequiredLength = 12;
    o.Password.RequireDigit = false;
    o.Password.RequireLowercase = false;
    o.Password.RequireUppercase = false;
    o.Password.RequireNonAlphanumeric = false;
    o.Lockout.AllowedForNewUsers = true;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
}).AddEntityFrameworkStores<WonderDbContext>().AddSignInManager().AddDefaultTokenProviders();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "wonder.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Configuration.GetValue("Authentication:RequireHttps", false)
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
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
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddHttpClient("models");
builder.Services.AddHybridCache(o => o.MaximumPayloadBytes = 5 * 1024 * 1024);
builder.Services.AddHttpClient("media", c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(MediaStore.CreateHandler)
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 1;
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
    });
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddSingleton<PromptComposer>();
builder.Services.AddSingleton<ContextBuilder>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConversationClock>();
builder.Services.AddSingleton<ConversationHistory>();
builder.Services.AddSingleton<ConversationTools>();
builder.Services.AddSingleton<InteractionBoundary>();
builder.Services.AddSingleton<EmotionEngine>();
builder.Services.AddSingleton<MessageIngestor>();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton(_ => new TokenCounter(Path.Combine(builder.Environment.ContentRootPath, "Tokenizer")));
builder.Services.AddSingleton<TokenBudget>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<MediaStore>();
builder.Services.AddScoped<ChatWorkflow>();
builder.Services.AddScoped<OutgoingWorkflow>();
builder.Services.AddScoped<ReflectionWorkflow>();
builder.Services.AddScoped<MemoryMaintenanceWorkflow>();
builder.Services.AddScoped<MaintenanceWorkflow>();
builder.Services.AddScoped<SummaryWorkflow>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<OneBotConnectionManager>();
builder.Services.AddSingleton<IPlatformConnection>(sp => sp.GetRequiredService<OneBotConnectionManager>());
if (builder.Configuration.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OneBotConnectionManager>());
    builder.Services.AddHostedService<JobWorkers>();
    builder.Services.AddWonderScheduling(builder.Configuration);
}

var app = builder.Build();
if (Array.IndexOf(args, "--export-event-schema") is var schemaIndex && schemaIndex >= 0)
{
    await File.WriteAllTextAsync(args[schemaIndex + 1],
        EventContracts.Schema().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return;
}

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

if (args.Contains("--export-retired-context"))
{
    Console.WriteLine(Json.Write(await app.Services.GetRequiredService<BackupService>().ExportRetiredContextAsync(CancellationToken.None)));
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
// Serve the SPA index for navigation; JS/CSS assets remain static files.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" || context.Request.Path == "/index.html") await ServeFrontend(context);
    else await next();
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (WonderDbContext db, CancellationToken ct) =>
{
    try
    {
        if (!await db.Database.CanConnectAsync(ct)) return Results.StatusCode(503);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception)
    {
        return Results.StatusCode(503);
    }
});
app.MapOpenApi().RequireAuthorization();
app.MapControllers();
app.MapFallback(ServeFrontend);
await app.RunAsync();

async Task ServeFrontend(HttpContext context)
{
    var path = context.Request.Path;
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/health") || path.StartsWithSegments("/openapi") ||
        (Path.HasExtension(path.Value) && path != "/index.html") || context.Request.Method is not ("GET" or "HEAD"))
    {
        context.Response.StatusCode = 404;
        return;
    }

    var index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(index))
    {
        context.Response.StatusCode = 404;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Method == "HEAD") return;
    var html = await File.ReadAllTextAsync(index, context.RequestAborted);
    await context.Response.WriteAsync(html, context.RequestAborted);
}

public partial class Program;
