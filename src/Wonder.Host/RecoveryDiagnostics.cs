using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Wonder.Core;

namespace Wonder.Host;

public sealed record RecoveryExceptionView(string Type, string Message, string[] StackTrace, RecoveryExceptionView? Inner);
public sealed record RecoveryDiagnostic(Guid ErrorId, DateTimeOffset OccurredAt, string Stage, string Title, RecoveryExceptionView Exception);

public static class RecoveryDiagnostics
{
    private static readonly HashSet<string> SafeCodes = new(StringComparer.Ordinal)
    {
        "IndependentRecoveryProtectionNotConfigured", "IndependentRecoveryAuthorityNotConfigured", "RecoverySchemaRequiresOfflineMigration",
        "RevokedMemoryPayloadRemains", "RecoveryMemoryFenceMissing", "RecoveryMessageFenceMissing", "RecoveryCompletionInvariantFailed",
        "RecoveryBaselineMismatch", "UnresolvedRecoveryIntent", "RecoveryAuthorityRejected", "MissingRecoveryReceipt",
        "RecoveryReceiptScopeMismatch", "RecoveryReceiptSignatureInvalid", "RecoveryTrustKeyInvalid", "RecoveryInstanceMismatch",
        "RecoveryArchiveKeyUnavailable", "RecoveryArchiveKeyInvalid", "RecoveryManifestInvalid", "RecoverySqliteInvalid",
        "RecoverySqliteIntegrityFailed", "RecoverySqliteIdentityMismatch", "UnknownRecoveryRecordKind", "RecoveryCoverageMismatch",
        "RecoverySnapshotScopeInvalid", "RecoverySnapshotTooLarge", "RecoveryProtectionScopeMismatch",
        "IndependentRecoveryCoverageUnverified", "RecoverySchemaMismatch", "RecoveryScopeIdentityMissing", "RecoveryOperationScopeMismatch",
        "RecoveryResourceVersionMismatch", "LegacyRecoveryScopeUnknown", "RecoveryLegacyFenceMissing", "RecoveryCompletedPayloadRemains", "RecoverySummaryFenceMissing"
    };
    public static RecoveryDiagnostic Capture(Exception exception, string stage) => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        stage, "由于发生灾难性故障，Wonder 无法启动", ExceptionView(exception, 0));

    private static RecoveryExceptionView ExceptionView(Exception exception, int depth)
    {
        var frames = new StackTrace(exception, true).GetFrames().Take(80).Select(frame =>
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType?.FullName ?? "UnknownMethod";
            // Real captured frames, with no arbitrary file paths or free-form exception Data.
            return "at " + type + "." + method?.Name + (type.StartsWith("Wonder.", StringComparison.Ordinal) && frame.GetFileLineNumber() > 0
                ? ":line " + frame.GetFileLineNumber() : "");
        }).ToArray();
        var safeMessage = exception is RecoveryUnavailableException known && SafeCodes.Contains(known.Code)
            ? known.Code : "[free-form exception message redacted]";
        return new RecoveryExceptionView(exception.GetType().FullName ?? exception.GetType().Name, safeMessage, frames,
            depth < 8 && exception.InnerException is { } inner ? ExceptionView(inner, depth + 1) : null);
    }

    public static async Task RunAsync(IConfiguration? configuration, Exception exception, string stage, CancellationToken ct = default)
    {
        var diagnostic = Capture(exception, stage);
        var hash = configuration?["Recovery:DiagnosticTokenHash"];
        int? port = int.TryParse(configuration?["Recovery:DiagnosticPort"], out var parsed) ? parsed : null;
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || port is null or < 1024 or > 65535)
        {
            // Restricted local CLI fallback: already redacted real diagnostics only, never raw Exception.ToString().
            Console.Error.WriteLine(Json.Write(diagnostic));
            Environment.ExitCode = 78;
            return;
        }
        try
        {
            var options = new WebApplicationOptions { Args = [], ApplicationName = typeof(RecoveryDiagnostics).Assembly.FullName };
            var builder = WebApplication.CreateSlimBuilder(options);
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:" + port.Value);
            await using var app = builder.Build();
            app.MapGet("/", () => Results.Json(new { diagnostic.ErrorId, diagnostic.Title, status = "RecoveryBlocked" }));
            app.MapGet("/diagnostics", (HttpContext context) =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                string? token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : null;
                if (authorization.StartsWith("Basic ", StringComparison.Ordinal))
                {
                    try
                    {
                        var basic = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..]));
                        if (basic.StartsWith("admin:", StringComparison.Ordinal)) token = basic[6..];
                    }
                    catch (FormatException) { }
                }
                if (token is null || token.Length > 256 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(hash)))
                {
                    context.Response.Headers.WWWAuthenticate = "Basic realm=WonderRecovery";
                    return Results.Unauthorized();
                }
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.Json(diagnostic);
            });
            // No controllers, ordinary authentication DB, SPA, OpenAPI, Worker, OneBot or Quartz are registered here.
            await app.RunAsync(ct);
        }
        catch (Exception diagnosticFailure)
        {
            Console.Error.WriteLine(Json.Write(Capture(diagnosticFailure, "DiagnosticStartup")));
            Console.Error.WriteLine(Json.Write(diagnostic));
            Environment.ExitCode = 78;
        }
    }
}
