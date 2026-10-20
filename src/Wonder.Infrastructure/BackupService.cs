using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Wonder.Core;

namespace Wonder.Infrastructure;

public sealed class BackupService(IDbContextFactory<WonderDbContext> factory, IConfiguration configuration)
{
    public async Task<object> CreateAsync(CancellationToken ct)
    {
        var recipient = configuration["Backups:AgeRecipient"];
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.StartsWith("age1", StringComparison.Ordinal))
            throw new ArgumentException("Configure an age recipient before backups.");
        var directory = Path.GetFullPath(configuration["Backups:Path"] ?? "data/backups");
        var ledgerDirectory = Path.GetFullPath(configuration["Backups:DeletionLedgerPath"] ?? "data/deletion-ledger");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(ledgerDirectory);
        var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var dumpPath = Path.Combine(directory, name + ".dump.age");
        var ledgerPath = Path.Combine(ledgerDirectory, name + ".ledger.json.age");
        var cs = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres"));
        var dumpInfo = new ProcessStartInfo("pg_dump")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[]
                 {
                     "--host", cs.Host!, "--port", cs.Port.ToString(), "--username", cs.Username!, "--dbname",
                     cs.Database!, "--format=custom", "--no-owner", "--no-acl"
                 }) dumpInfo.ArgumentList.Add(arg);
        dumpInfo.Environment["PGPASSWORD"] = cs.Password;
        using var age = StartAge(recipient, dumpPath);
        using var dump = Process.Start(dumpInfo) ?? throw new IOException("pg_dump could not start.");
        var dumpError = dump.StandardError.ReadToEndAsync(ct);
        var ageError = age.StandardError.ReadToEndAsync(ct);
        try
        {
            await dump.StandardOutput.BaseStream.CopyToAsync(age.StandardInput.BaseStream, ct);
            age.StandardInput.Close();
            await dump.WaitForExitAsync(ct);
            await age.WaitForExitAsync(ct);
            await dumpError;
            await ageError;
            if (dump.ExitCode != 0 || age.ExitCode != 0) throw new IOException("Encrypted PostgreSQL backup failed.");
            await using var db = await factory.CreateDbContextAsync(ct);
            // Keep this file on a separate volume, and export it again after subsequent deletions.
            var ledger = await db.Revocations.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);
            using var ledgerAge = StartAge(recipient, ledgerPath);
            var ledgerError = ledgerAge.StandardError.ReadToEndAsync(ct);
            await JsonSerializer.SerializeAsync(ledgerAge.StandardInput.BaseStream, ledger, Json.Options, ct);
            ledgerAge.StandardInput.Close();
            await ledgerAge.WaitForExitAsync(ct);
            await ledgerError;
            if (ledgerAge.ExitCode != 0) throw new IOException("Deletion ledger backup failed.");
            return new
            {
                id = name, database = Path.GetFileName(dumpPath), deletionLedger = Path.GetFileName(ledgerPath),
                createdAt = DateTimeOffset.UtcNow
            };
        }
        catch
        {
            if (!dump.HasExited) dump.Kill(true);
            if (!age.HasExited) age.Kill(true);
            if (File.Exists(dumpPath)) File.Delete(dumpPath);
            if (File.Exists(ledgerPath)) File.Delete(ledgerPath);
            throw;
        }
    }

    public async Task<object> ExportLedgerAsync(CancellationToken ct)
    {
        var recipient = configuration["Backups:AgeRecipient"];
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.StartsWith("age1", StringComparison.Ordinal))
            throw new ArgumentException("Configure an age recipient first.");
        var directory = Path.GetFullPath(configuration["Backups:DeletionLedgerPath"] ?? "data/deletion-ledger");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".ledger.json.age");
        await using var db = await factory.CreateDbContextAsync(ct);
        var ledger = await db.Revocations.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);
        using var age = StartAge(recipient, file);
        var stderr = age.StandardError.ReadToEndAsync(ct);
        try
        {
            await JsonSerializer.SerializeAsync(age.StandardInput.BaseStream, ledger, Json.Options, ct);
            age.StandardInput.Close();
            await age.WaitForExitAsync(ct);
            await stderr;
            if (age.ExitCode != 0) throw new IOException("Deletion ledger export failed.");
            return new { file = Path.GetFileName(file), createdAt = DateTimeOffset.UtcNow };
        }
        catch
        {
            if (!age.HasExited) age.Kill(true);
            if (File.Exists(file)) File.Delete(file);
            throw;
        }
    }

    private static Process StartAge(string recipient, string output)
    {
        var info = new ProcessStartInfo("age")
        {
            RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-r", recipient, "-o", output }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("age could not start.");
    }
}