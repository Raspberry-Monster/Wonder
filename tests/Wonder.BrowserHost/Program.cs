using Microsoft.Extensions.FileProviders;

// Static browser-test harness only. No business DI, database, model, Worker, QQ or recovery bypass.
var builder = WebApplication.CreateSlimBuilder(args);
if (!builder.Environment.IsEnvironment("BrowserTest")) throw new InvalidOperationException("BrowserTest environment required.");
var root = Path.GetFullPath(builder.Configuration["StaticRoot"] ?? throw new ArgumentException("StaticRoot required."));
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(root) });
app.MapGet("/health/live", () => Results.Ok(new { status = "browser-test-only" }));
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/health") ||
        context.Request.Path.StartsWithSegments("/openapi") || Path.HasExtension(context.Request.Path))
    { context.Response.StatusCode = 404; return; }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(root, "index.html"));
});
await app.RunAsync();
