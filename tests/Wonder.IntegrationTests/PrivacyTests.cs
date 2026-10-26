using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class PrivacyTests
{
    [Test]
    public async Task ForgetCleansOnlyBoundMemoryPayloadsAndHasDurableSynchronousReceipt()
    {
        await using var pg = await TestPostgres.CreateAsync();
        var authority = new TestAuthority();
        using var app = App(pg, authority);
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var result = await service.ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        await Assert.That(result.Completed).IsTrue();
        await Assert.That(result.RecoveryVerified && result.CleanupVerified && result.Fenced).IsTrue();
        var replay = await service.ForgetAsync(f.User, f.Old.Value, 1, "forget", default);
        await Assert.That(replay.OperationId).IsEqualTo(result.OperationId);
        await Assert.That(async () => { await service.ForgetAsync(f.User, f.Old.Value, 2, "forget", default); })
            .ThrowsExactly<DbUpdateConcurrencyException>();
        var memory = await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.Old);
        await Assert.That(memory.Text).IsEqualTo("");
        await Assert.That(await db.Embeddings.AnyAsync(x => x.MemoryId == f.Old)).IsFalse();
        var source = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == f.Evidence);
        await Assert.That(source.Text).IsEqualTo("喜欢蓝色，也弹钢琴。");
        await Assert.That(source.ContextInvalidated).IsFalse();
        await Assert.That(await ContentSources.MessageValidAsync(db, source, default)).IsFalse();
        var proposal = Json.Read<ReflectionProposal>((await db.Reflections.AsNoTracking().SingleAsync()).Payload);
        await Assert.That(proposal.Memories).IsEmpty();
        await Assert.That(proposal.Relationships.Single().Preference).IsEqualTo("喜欢钢琴");
        await Assert.That(proposal.Emotion!.Reason).IsEqualTo("independent emotion");
        await Assert.That((await db.Relationships.SingleAsync()).PreferencesJson).IsEqualTo("[\"independent relationship\"]");
        await Assert.That((await db.Notes.SingleAsync()).Content).IsEqualTo("independent note");
        await Assert.That((await db.Summaries.SingleAsync()).Text).IsEqualTo("original summary");
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Text).IsEqualTo("");
        using var restarted = App(pg, authority);
        using var scope2 = restarted.Services.CreateScope();
        await Assert.That((await scope2.ServiceProvider.GetRequiredService<PrivacyWorkflow>().GetAsync(f.User, result.OperationId, default)).Completed).IsTrue();
        await Assert.That(authority.Prepares).IsEqualTo(1);
        await Assert.That(authority.LastArchive).IsNotNull();
    }

    [Test]
    public async Task MissingPrepareMakesNoDatabaseFenceOrPhysicalChange()
    {
        await using var pg = await TestPostgres.CreateAsync();
        var authority = new TestAuthority { FailPrepare = true };
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>().ForgetAsync(f.User, f.Old!.Value, 1, "prepare-fails", default); })
            .ThrowsExactly<RecoveryUnavailableException>();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That((await db.Memories.SingleAsync(x => x.Id == f.Old)).Text).IsEqualTo("喜欢蓝色");
        await Assert.That(await db.MemoryOperations.AnyAsync()).IsFalse();
    }

    [Test]
    public async Task MissingProtectionAckReturnsPersistentPendingAndResumesWithoutLosingIndependentProposals()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority { FailProtect = true };
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var first = await privacy.ForgetAsync(f.User, f.Old!.Value, 1, "pending", default);
        await Assert.That(first.Fenced).IsTrue(); await Assert.That(first.Completed).IsFalse();
        await Assert.That(first.RecoveryVerified).IsFalse();
        await Assert.That((await db.MemoryCandidates.AsNoTracking().SingleAsync()).Text).IsEqualTo("喜欢蓝色");
        authority.FailProtect = false;
        var job = await db.Outbox.AsNoTracking().SingleAsync(x => x.Type == JobKind.MemoryPrivacy);
        await privacy.RunAsync(job.Id, default);
        var done = await privacy.GetAsync(f.User, first.OperationId, default);
        await Assert.That(done.Completed).IsTrue();
        await Assert.That(done.OperationId).IsEqualTo(first.OperationId);
        await Assert.That((await db.Relationships.SingleAsync()).PreferencesJson).IsEqualTo("[\"independent relationship\"]");
    }

    [Test]
    public async Task EraseFreezesWholeMessageScopeAndReportsSharedMediaAsPartialThenResumes()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority();
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var root = scope.ServiceProvider.GetRequiredService<MediaStore>().Root; Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N")); await File.WriteAllTextAsync(path, "private-media");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var asset = new MediaAsset { BotId = f.Bot, MessageId = f.Evidence, Path = path, Hash = hash, ContentHash = hash };
        var unrelated = new ChatMessage { BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session, SenderId = f.User,
            Text = "unrelated", ExternalMessageId = "unrelated", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.Add(unrelated); db.Media.Add(asset);
        var shared = new MediaAsset { BotId = f.Bot, MessageId = unrelated.Id, Path = path, Hash = hash }; db.Media.Add(shared);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var preview = await service.PreviewAsync(f.User, f.Old!.Value, 1, [f.Evidence], default);
        await Assert.That(preview.Messages.Single().Text).Contains("也弹钢琴");
        await Assert.That(preview.Warning).Contains("complete selected messages");
        var result = await service.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "erase", default);
        await Assert.That(result.Completed).IsFalse();
        await Assert.That(result.Items.Any(x => x.Kind == "Media" && x.State == "NeedsReview")).IsTrue();
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == f.Evidence)).Text).IsEqualTo("");
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == unrelated.Id)).Text).IsEqualTo("unrelated");
        await Assert.That(File.Exists(path)).IsTrue();
        await db.Media.Where(x => x.Id == shared.Id).ExecuteDeleteAsync();
        await service.RetryAsync(f.User, result.OperationId, default);
        var job = await db.Outbox.AsNoTracking().Where(x => x.Type == JobKind.MemoryPrivacy).OrderByDescending(x => x.Sequence).FirstAsync();
        await service.RunAsync(job.Id, default);
        await Assert.That((await service.GetAsync(f.User, result.OperationId, default)).Completed).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That((await db.Notes.SingleAsync()).Content).IsEqualTo("independent note");
    }

    [Test]
    public async Task ExpiredOrChangedErasePreviewAndForeignIdentityCannotAuthorizeDeletion()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority();
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var preview = await privacy.PreviewAsync(f.User, f.Old!.Value, 1, [f.Evidence], default);
        await db.MemoryOperations.Where(x => x.Id == preview.PreviewId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviewExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Assert.That(async () => { await privacy.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "expired", default); }).ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That(async () => { await privacy.EraseAsync(Guid.NewGuid(), preview.PreviewId, preview.ScopeHash, "foreign", default); }).ThrowsExactly<KeyNotFoundException>();
        var fresh = await privacy.PreviewAsync(f.User, f.Old.Value, 1, [f.Evidence], default);
        await db.Messages.Where(x => x.Id == f.Evidence).ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "changed original"));
        await Assert.That(async () => { await privacy.EraseAsync(f.User, fresh.PreviewId, fresh.ScopeHash, "changed", default); }).ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That(authority.Prepares).IsEqualTo(0);
    }

    [Test]
    [Arguments("quoted", false)]
    [Arguments("confirmation", true)]
    [Arguments("current", true)]
    [Arguments("independent", true)]
    [Arguments("uncertain", false)]
    public async Task OldQuotesAreNotNewEvidenceButCurrentConfirmationAndIndependentFactsAre(string kind, bool expected)
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority();
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>().ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var text = kind switch { "quoted" => "以前我说过“喜欢蓝色”。", "confirmation" => "以前我说过“喜欢蓝色”，现在仍然喜欢蓝色。",
            "independent" => "引用“喜欢蓝色”。我现在弹钢琴。", "uncertain" => "还是老样子吧（笑）", _ => "我现在喜欢蓝色。" };
        var message = new ChatMessage { BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            SenderId = f.User, Role = MessageRole.User, Text = text, AllowReflection = true, ExternalMessageId = "fresh-" + kind,
            SegmentsJson = Json.Write(new[] { new MessageSegment(SegmentKind.ReplyReference, "source") }), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        db.Messages.Add(message); await db.SaveChangesAsync();
        var anchor = kind == "independent" ? "我现在弹钢琴" : kind == "current" ? "我现在喜欢蓝色" : kind == "confirmation" ? "现在仍然喜欢蓝色" : text;
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        var role = kind == "confirmation" ? "CurrentConfirmation" : kind == "uncertain" ? "Uncertain" : kind == "quoted" ? "HistoricalQuote" : "CurrentStatement";
        var assertion = new EvidenceAssertion(message.Id, role, start, anchor.Length, anchor,
            kind == "confirmation" ? start : null, kind == "confirmation" ? anchor : null);
        var candidate = new MemoryCandidate(kind == "independent" ? "piano" : "color", anchor, true, false, [message.Id], [assertion]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, candidate, default)).IsEqualTo(expected);
        var old = new MemoryCandidate("color", "喜欢蓝色", true, false, [f.Evidence]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, old, default)).IsFalse();
    }

    [Test]
    public async Task EncryptedSqliteRoundTripRejectsWrongKeyTamperingAndInstanceAndContainsNoDeletedBody()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority();
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var packages = scope.ServiceProvider.GetRequiredService<RecoveryPackages>();
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>().ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var archive = authority.LastArchive!;
        var directory = Path.GetFullPath(".local/recovery-roundtrip-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var encryptedPath = Path.Combine(directory, "protection.db.aesgcm"); var manifestPath = Path.Combine(directory, "manifest.json");
        await File.WriteAllBytesAsync(encryptedPath, archive.Ciphertext); await File.WriteAllTextAsync(manifestPath, archive.ManifestJson);
        archive = new RecoveryArchive(await File.ReadAllBytesAsync(encryptedPath), await File.ReadAllTextAsync(manifestPath), archive.Digest);
        var snapshot = packages.Decode(archive, authority.InstanceId);
        await Assert.That(snapshot.Operations.Length).IsEqualTo(1);
        await Assert.That(Json.Write(snapshot)).DoesNotContain("喜欢蓝色");
        await Assert.That(archive.ManifestJson).DoesNotContain("喜欢蓝色");
        await Assert.That(() => packages.Decode(archive, Guid.NewGuid())).ThrowsExactly<RecoveryUnavailableException>();
        var corrupt = archive.Ciphertext.ToArray(); corrupt[0] ^= 1;
        await Assert.That(() => packages.Decode(archive with { Ciphertext = corrupt }, authority.InstanceId)).ThrowsExactly<RecoveryUnavailableException>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Recovery:ArchiveKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Recovery:ArchiveKeyId"] = "isolated-test-key", ["Recovery:TrustedBaseline"] = "isolated-test-baseline" }).Build();
        await Assert.That(() => new RecoveryPackages(config).Decode(archive, authority.InstanceId)).Throws<CryptographicException>();
    }

    [Test]
    public async Task PreciseRecoveryReplayTwiceDoesNotDeleteLaterIndependentFacts()
    {
        await using var pg = await TestPostgres.CreateAsync(); var authority = new TestAuthority();
        using var app = App(pg, authority); var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var original = await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.Old);
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>().ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var archive = authority.LastArchive!;
        // Simulate an older row being restored ONLY inside this disposable PG database.
        await db.Memories.Where(x => x.Id == f.Old).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, 1).SetProperty(x => x.Status, MemoryStatus.Active)
            .SetProperty(x => x.Text, original.Text).SetProperty(x => x.FactKey, original.FactKey));
        var later = new Memory { BotId = f.Bot, SubjectId = f.User, Text = "later lawful confirmation", FactKey = original.FactKey };
        db.Memories.Add(later); await db.SaveChangesAsync();
        var replay = scope.ServiceProvider.GetRequiredService<RecoveryReplay>();
        await replay.ReplayAsync(archive, default); await replay.ReplayAsync(archive, default);
        await Assert.That((await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.Old)).Text).IsEqualTo("");
        await Assert.That((await db.Memories.AsNoTracking().SingleAsync(x => x.Id == later.Id)).Text).IsEqualTo(later.Text);
    }

    internal static TestApp App(TestPostgres pg, TestAuthority authority) => new(environment: "MemoryLifecycleTest", postgresConnection: pg.ConnectionString, recoveryAuthority: authority);

    internal static async Task<MemoryMaintenanceTests.Fixture> SeedAsync(TestApp app)
    {
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var original = await db.Messages.SingleAsync(x => x.Id == f.Evidence); original.Text = "喜欢蓝色，也弹钢琴。"; original.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        var candidate = new MemoryCandidate("color", "喜欢蓝色", true, false, [original.Id]);
        var reflection = await db.Reflections.SingleAsync();
        reflection.Payload = Json.Write(new ReflectionProposal(false, new EmotionProposal(.1, 0, 0, "independent emotion", [original.Id]),
            [new RelationshipProposal("喜欢钢琴", true, [original.Id])], [candidate]));
        var memory = await db.Memories.SingleAsync(x => x.Id == f.Old); memory.Text = candidate.Text; memory.FactKey = candidate.FactKey;
        memory.EvidenceJson = Json.Write(candidate.EvidenceMessageIds); memory.SourceReflectionId = reflection.Id;
        var pending = await db.MemoryCandidates.SingleAsync(); pending.Text = candidate.Text; pending.FactKey = candidate.FactKey;
        pending.CandidateHash = Json.Hash(Json.Write(candidate)); pending.ResultMemoryId = memory.Id; pending.SourceReflectionId = reflection.Id;
        db.Relationships.Add(new RelationshipState { BotId = f.Bot, UserId = f.User, PreferencesJson = "[\"independent relationship\"]" });
        db.Notes.Add(new Note { BotId = f.Bot, UserId = f.User, ConversationId = f.Conversation, Content = "independent note" });
        db.Summaries.Add(new ConversationSummary { BotId = f.Bot, SessionId = f.Session, Revision = 1, Text = "original summary", MessageIdsJson = Json.Write(new[] { original.Id }) });
        await db.SaveChangesAsync(); return f;
    }

    internal sealed class TestAuthority : IRecoveryAuthority
    {
        private readonly Dictionary<Guid, ProtectionIntent> intents = [];
        public bool Configured => true;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public bool FailPrepare { get; set; }
        public bool FailProtect { get; set; }
        public RecoveryProof? StartupProof { get; set; }
        public int Prepares { get; private set; }
        public RecoveryArchive? LastArchive { get; private set; }
        public Task<ProtectionReceipt> PrepareAsync(ProtectionIntent intent, CancellationToken ct)
        { if (FailPrepare) throw new RecoveryUnavailableException("TestPrepareFailure"); lock (intents) { intents[intent.OperationId] = intent; Prepares++; } return Receipt(intent, "Prepare"); }
        public Task<ProtectionReceipt> ResolveAsync(ProtectionIntent intent, bool committed, CancellationToken ct) => Receipt(intent, committed ? "Commit" : "Abort");
        public Task<ProtectionReceipt> ProtectAsync(ProtectionIntent intent, RecoveryArchive archive, CancellationToken ct)
        { if (FailProtect) throw new RecoveryUnavailableException("TestProtectionFailure"); LastArchive = archive; return ProtectReceipt(intent, archive); }
        private async Task<ProtectionReceipt> ProtectReceipt(ProtectionIntent intent, RecoveryArchive archive) =>
            (await Receipt(intent, "Protect")) with { ArchiveDigest = archive.Digest, ManifestDigest = Json.Hash(archive.ManifestJson) };
        public Task<RecoveryProof> VerifyAsync(RecoveryCoverage coverage, CancellationToken ct) => Task.FromResult(StartupProof ?? new RecoveryProof(true, "SimulatedIndependentTestAuthority"));
        private Task<ProtectionReceipt> Receipt(ProtectionIntent intent, string phase)
        {
            lock (intents) if (!intents.TryGetValue(intent.OperationId, out var known) || Json.Write(known) != Json.Write(intent))
                throw new RecoveryUnavailableException("TestIntentMismatch");
            return Task.FromResult(new ProtectionReceipt(intent.OperationId, Json.Hash(Json.Write(intent)), phase, Prepares, "test-receipt-only"));
        }
    }
}
