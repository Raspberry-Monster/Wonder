using System.Data;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SQLitePCL;
using Wonder.Core;

namespace Wonder.Infrastructure;

public sealed record RecoveryItem(string Kind, Guid Id, string Version, string State);
public sealed record RecoveryOperation(ProtectionIntent Intent, string[] AssertionHashes, RecoveryItem[] Items,
    string IdempotencyKey, string ArgumentsHash, Guid TargetMemoryId, long ExpectedRevision);
public sealed record RecoverySnapshot(int FormatVersion, Guid InstanceId, string Schema, RecoveryOperation[] Operations, MemoryRevocation[] Legacy);
public sealed record RecoveryManifest(int FormatVersion, Guid InstanceId, string Schema, string KeyId,
    string Nonce, string Tag, string CiphertextDigest, string OperationsHash, string Baseline, int RecordCount);

public sealed class RecoveryPackages(IConfiguration configuration)
{
    public bool Configured
    {
        get
        {
            if (string.IsNullOrWhiteSpace(configuration["Recovery:ArchiveKeyId"]) || string.IsNullOrWhiteSpace(configuration["Recovery:TrustedBaseline"])) return false;
            try { var key = GetKey(); CryptographicOperations.ZeroMemory(key); return true; }
            catch (RecoveryUnavailableException) { return false; }
        }
    }

    public async Task<ProtectionIntent> IntentAsync(WonderDbContext db, MemoryOperation operation, Guid instance, CancellationToken ct)
    {
        var rows = await db.EvidenceSuppressions.AsNoTracking().Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.MemoryId).ThenBy(x => x.MessageId).ToListAsync(ct);
        var suppressions = rows.Select(x => new SuppressedEvidence(x.MemoryId, x.MessageId, x.MessageVersion, x.FactKeyHash, Json.Read<EvidenceSpan[]>(x.SpansJson))).ToArray();
        return new ProtectionIntent(operation.Id, instance, operation.ActorId, operation.BotId, operation.Kind, operation.ScopeHash,
            Json.Read<PrivacyTarget[]>(operation.TargetsJson), suppressions);
    }

    public async Task<RecoveryArchive> CreateAsync(WonderDbContext db, Guid actor, Guid instance, CancellationToken ct)
    {
        if (!Configured) throw new RecoveryUnavailableException("RecoveryArchiveKeyUnavailable");
        // One consistent cut across operations, targets, suppression and existing typed ledger.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var operations = await db.MemoryOperations.AsNoTracking().Where(x => x.Fenced && (x.Kind == "Forget" || x.Kind == "Erase"))
            .OrderBy(x => x.Id).ToListAsync(ct);
        var records = new List<RecoveryOperation>();
        foreach (var operation in operations)
        {
            var intent = await IntentAsync(db, operation, instance, ct);
            var items = await db.MemoryOperationItems.AsNoTracking().Where(x => x.OperationId == operation.Id).OrderBy(x => x.Kind).ThenBy(x => x.ResourceId)
                .Select(x => new RecoveryItem(x.Kind, x.ResourceId, x.SourceVersion, x.State)).ToArrayAsync(ct);
            records.Add(new RecoveryOperation(intent, Json.Read<string[]>(operation.AssertionHashesJson), items,
                operation.IdempotencyKey, operation.ArgumentsHash, operation.TargetMemoryId, operation.ExpectedRevision));
        }
        var schema = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "Unknown";
        var legacy = await db.Revocations.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return Encode(new RecoverySnapshot(1, instance, schema, records.ToArray(), legacy));
    }

    public static string CoverageHash(IEnumerable<ProtectionIntent> intents) => Json.Hash(Json.Write(intents
        .OrderBy(x => x.OperationId).Select(x => new { x.OperationId, hash = Json.Hash(Json.Write(x)) })));

    public RecoveryArchive Encode(RecoverySnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        var key = GetKey();
        byte[]? plain = null;
        try
        {
            Batteries_V2.Init();
            using var sqlite = new SqliteConnection("Data Source=:memory:");
            sqlite.Open();
            using (var command = sqlite.CreateCommand())
            {
                command.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA journal_mode=MEMORY; CREATE TABLE PackageInfo (Id INTEGER PRIMARY KEY CHECK (Id=1), FormatVersion INTEGER NOT NULL, InstanceId TEXT NOT NULL, SchemaVersion TEXT NOT NULL); CREATE TABLE RecoveryRecords (Kind TEXT NOT NULL, Id TEXT NOT NULL, MetadataJson TEXT NOT NULL, PRIMARY KEY(Kind,Id));";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO PackageInfo VALUES (1,1,$instance,$schema)";
                command.Parameters.AddWithValue("$instance", snapshot.InstanceId.ToString()); command.Parameters.AddWithValue("$schema", snapshot.Schema);
                command.ExecuteNonQuery();
            }
            foreach (var operation in snapshot.Operations) Insert(sqlite, "Operation", operation.Intent.OperationId, Json.Write(operation));
            foreach (var legacy in snapshot.Legacy) Insert(sqlite, "Legacy", legacy.Id, Json.Write(legacy));
            var pointer = raw.sqlite3_serialize(sqlite.Handle, "main", out var size, 0);
            if (pointer == IntPtr.Zero || size <= 0 || size > 64 * 1024 * 1024) throw new RecoveryUnavailableException("RecoverySnapshotTooLarge");
            plain = new byte[checked((int)size)];
            try { Marshal.Copy(pointer, plain, 0, plain.Length); }
            finally { Marshal.Copy(new byte[plain.Length], 0, pointer, plain.Length); raw.sqlite3_free(pointer); }
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16]; var ciphertext = new byte[plain.Length];
            var manifest = new RecoveryManifest(1, snapshot.InstanceId, snapshot.Schema, configuration["Recovery:ArchiveKeyId"]!,
                Convert.ToBase64String(nonce), "", "", CoverageHash(snapshot.Operations.Select(x => x.Intent)),
                configuration["Recovery:TrustedBaseline"]!, snapshot.Operations.Length + snapshot.Legacy.Length);
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, ciphertext, tag, Encoding.UTF8.GetBytes(Json.Write(manifest)));
            manifest = manifest with { Tag = Convert.ToBase64String(tag), CiphertextDigest = Convert.ToHexString(SHA256.HashData(ciphertext)) };
            var archive = new RecoveryArchive(ciphertext, Json.Write(manifest), manifest.CiphertextDigest);
            // Verify actual decryption and SQLite integrity before the authority may acknowledge storage.
            _ = Decode(archive, snapshot.InstanceId);
            return archive;
        }
        finally { CryptographicOperations.ZeroMemory(key); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public RecoverySnapshot Decode(RecoveryArchive archive, Guid expectedInstance)
    {
        var manifest = Json.Read<RecoveryManifest>(archive.ManifestJson);
        if (manifest.FormatVersion != 1 || manifest.InstanceId != expectedInstance || manifest.RecordCount < 0 ||
            manifest.KeyId != configuration["Recovery:ArchiveKeyId"] || manifest.Baseline != configuration["Recovery:TrustedBaseline"] ||
            archive.Ciphertext.Length > 64 * 1024 * 1024 || manifest.CiphertextDigest != archive.Digest ||
            manifest.CiphertextDigest != Convert.ToHexString(SHA256.HashData(archive.Ciphertext)))
            throw new RecoveryUnavailableException("RecoveryManifestInvalid");
        var key = GetKey(); var plain = new byte[archive.Ciphertext.Length];
        var pointer = IntPtr.Zero;
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(Convert.FromBase64String(manifest.Nonce), archive.Ciphertext,
                Convert.FromBase64String(manifest.Tag), plain, Encoding.UTF8.GetBytes(Json.Write(manifest with { Tag = "", CiphertextDigest = "" })));
            Batteries_V2.Init();
            pointer = Marshal.AllocHGlobal(plain.Length); Marshal.Copy(plain, 0, pointer, plain.Length);
            RecoverySnapshot snapshot;
            using (var sqlite = new SqliteConnection("Data Source=:memory:"))
            {
                sqlite.Open();
                if (raw.sqlite3_deserialize(sqlite.Handle, "main", pointer, plain.Length, plain.Length, 4) != raw.SQLITE_OK)
                    throw new RecoveryUnavailableException("RecoverySqliteInvalid");
                using var command = sqlite.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
                if ((string?)command.ExecuteScalar() != "ok") throw new RecoveryUnavailableException("RecoverySqliteIntegrityFailed");
                command.CommandText = "SELECT FormatVersion, InstanceId, SchemaVersion FROM PackageInfo WHERE Id=1";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read() || reader.GetInt32(0) != 1 || reader.GetString(1) != expectedInstance.ToString() || reader.GetString(2) != manifest.Schema)
                        throw new RecoveryUnavailableException("RecoverySqliteIdentityMismatch");
                }
                var operations = new List<RecoveryOperation>(); var legacy = new List<MemoryRevocation>();
                command.CommandText = "SELECT Kind, MetadataJson FROM RecoveryRecords ORDER BY Kind, Id";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        if (reader.GetString(0) == "Operation") operations.Add(Json.Read<RecoveryOperation>(reader.GetString(1)));
                        else if (reader.GetString(0) == "Legacy") legacy.Add(Json.Read<MemoryRevocation>(reader.GetString(1)));
                        else throw new RecoveryUnavailableException("UnknownRecoveryRecordKind");
                snapshot = new RecoverySnapshot(1, expectedInstance, manifest.Schema, operations.ToArray(), legacy.ToArray());
            }
            ValidateSnapshot(snapshot);
            if (snapshot.Operations.Length + snapshot.Legacy.Length != manifest.RecordCount ||
                CoverageHash(snapshot.Operations.Select(x => x.Intent)) != manifest.OperationsHash)
                throw new RecoveryUnavailableException("RecoveryCoverageMismatch");
            return snapshot;
        }
        finally
        {
            if (pointer != IntPtr.Zero) { Marshal.Copy(new byte[plain.Length], 0, pointer, plain.Length); Marshal.FreeHGlobal(pointer); }
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] GetKey()
    {
        try
        {
            var key = Convert.FromBase64String(configuration["Recovery:ArchiveKeyBase64"] ?? "");
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new RecoveryUnavailableException("RecoveryArchiveKeyInvalid"); }
            return key;
        }
        catch (FormatException) { throw new RecoveryUnavailableException("RecoveryArchiveKeyInvalid"); }
    }

    private static void Insert(SqliteConnection connection, string kind, Guid id, string json)
    {
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO RecoveryRecords VALUES($kind,$id,$json)";
        command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
    }

    private static void ValidateSnapshot(RecoverySnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1 || snapshot.InstanceId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.Schema) ||
            snapshot.Operations.Select(x => x.Intent.OperationId).Distinct().Count() != snapshot.Operations.Length ||
            snapshot.Operations.Any(x => x.Intent.InstanceId != snapshot.InstanceId || x.Intent.Kind is not ("Forget" or "Erase") ||
                x.Intent.Targets.Length == 0 || x.Intent.ScopeHash.Length != 64 || x.Intent.ActorId == Guid.Empty || x.Intent.BotId == Guid.Empty ||
                string.IsNullOrWhiteSpace(x.IdempotencyKey) || x.IdempotencyKey.Length > 128 || x.ArgumentsHash.Length != 64 || x.ExpectedRevision < 1 ||
                x.Intent.Targets.DistinctBy(t => (t.Kind, t.Id)).Count() != x.Intent.Targets.Length ||
                !x.Intent.Targets.All(t => t.Id != Guid.Empty && !string.IsNullOrWhiteSpace(t.Version)) ||
                !x.Items.Select(i => (i.Kind, i.Id, i.Version)).OrderBy(i => i.Kind).ThenBy(i => i.Id)
                    .SequenceEqual(x.Intent.Targets.Select(t => (t.Kind, t.Id, t.Version)).OrderBy(i => i.Kind).ThenBy(i => i.Id))))
            throw new RecoveryUnavailableException("RecoverySnapshotScopeInvalid");
        foreach (var operation in snapshot.Operations) ValidateOperation(operation, snapshot.InstanceId);
        _ = DeletionLedger.Read(Json.Write(snapshot.Legacy));
    }

    // Also used on startup: a Completed flag with missing/extra items is not a valid manifest.
    public static void ValidateOperation(RecoveryOperation record, Guid instance)
    {
        var intent = record.Intent;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "Memory", "Candidate", "ReflectionMemory", "ReflectionDraft", "SummaryDraft", "SummaryProjection" };
        if (intent.Kind == "Erase") allowed.UnionWith(["Message", "Summary", "Media", "OutgoingPart"]);
        if (intent.InstanceId != instance || instance == Guid.Empty || intent.OperationId == Guid.Empty || intent.Kind is not ("Forget" or "Erase") ||
            intent.Targets.Length == 0 || intent.ActorId == Guid.Empty || intent.BotId == Guid.Empty ||
            !intent.Targets.Any(t => t.Kind == "Memory" && t.Id == record.TargetMemoryId) ||
            intent.Targets.Any(t => t.Id == Guid.Empty || !allowed.Contains(t.Kind) || string.IsNullOrWhiteSpace(t.Version)) ||
            intent.Targets.DistinctBy(t => (t.Kind, t.Id)).Count() != intent.Targets.Length ||
            !record.Items.Select(i => (i.Kind, i.Id, i.Version)).OrderBy(i => i.Kind).ThenBy(i => i.Id)
                .SequenceEqual(intent.Targets.Select(t => (t.Kind, t.Id, t.Version)).OrderBy(i => i.Kind).ThenBy(i => i.Id)) ||
            record.Items.Any(i => i.State is not ("Pending" or "Processing" or "Completed" or "NeedsReview" or "Failed")) ||
            intent.Suppressions.DistinctBy(s => (s.MemoryId, s.MessageId)).Count() != intent.Suppressions.Length ||
            intent.Suppressions.Any(s => s.MessageId == Guid.Empty || s.FactKeyHash.Length != 64 || !s.FactKeyHash.All(Uri.IsHexDigit) ||
                !intent.Targets.Any(t => t.Kind == "Memory" && t.Id == s.MemoryId) ||
                s.Spans?.Any(p => p.Start < 0 || p.Length < 1 || p.Start > int.MaxValue - p.Length) == true))
            throw new RecoveryUnavailableException("RecoverySnapshotScopeInvalid");
    }
}
