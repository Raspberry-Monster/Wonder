using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
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
        using var app = App(pg);
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var result = await service.ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        await Assert.That(result.Completed).IsTrue();
        await Assert.That(result.CleanupVerified && result.Fenced).IsTrue();
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
        await Assert.That((await db.RelationshipClaims.SingleAsync()).Text)
            .IsEqualTo("independent relationship");
        await Assert.That((await db.Notes.SingleAsync()).Content).IsEqualTo("independent note");
        await Assert.That((await db.Summaries.SingleAsync()).Text).IsEqualTo("original summary");
        await Assert.That((await db.MemoryCandidates.SingleAsync()).Text).IsEqualTo("");
        using var restarted = App(pg);
        using var scope2 = restarted.Services.CreateScope();
        await Assert
            .That((await scope2.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
                .GetAsync(f.User, result.OperationId, default)).Completed).IsTrue();
    }

    [Test]
    public async Task EraseFreezesWholeMessageScopeAndReportsSharedMediaAsPartialThenResumes()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = App(pg);
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var root = scope.ServiceProvider.GetRequiredService<MediaStore>().Root;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, "private-media");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        var asset = new MediaAsset
            { BotId = f.Bot, MessageId = f.Evidence, Path = path, Hash = hash, ContentHash = hash };
        var unrelated = new ChatMessage
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session, SenderId = f.User,
            Text = "unrelated", ExternalMessageId = "unrelated", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.Add(unrelated);
        db.Media.Add(asset);
        var shared = new MediaAsset { BotId = f.Bot, MessageId = unrelated.Id, Path = path, Hash = hash };
        db.Media.Add(shared);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var preview = await service.PreviewAsync(f.User, f.Old!.Value, 1, [f.Evidence], default);
        await Assert.That(preview.Messages.Single().Text).Contains("也弹钢琴");
        await Assert.That(preview.Warning).Contains("complete selected messages");
        var result = await service.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "erase", default);
        await Assert.That(result.Completed).IsFalse();
        await Assert.That(result.Items.Any(x => x.Kind == "Media" && x.State == "NeedsReview")).IsTrue();
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == f.Evidence)).Text).IsEqualTo("");
        await Assert.That((await db.Messages.AsNoTracking().SingleAsync(x => x.Id == unrelated.Id)).Text)
            .IsEqualTo("unrelated");
        await Assert.That(File.Exists(path)).IsTrue();
        await db.Media.Where(x => x.Id == shared.Id).ExecuteDeleteAsync();
        await service.RetryAsync(f.User, result.OperationId, default);
        var job = await db.Outbox.AsNoTracking().Where(x => x.Type == JobKind.MemoryPrivacy)
            .OrderByDescending(x => x.Sequence).FirstAsync();
        await service.RunAsync(job.Id, default);
        await Assert.That((await service.GetAsync(f.User, result.OperationId, default)).Completed).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That((await db.Notes.SingleAsync()).Content).IsEqualTo("independent note");
    }

    [Test]
    public async Task ExpiredOrChangedErasePreviewAndForeignIdentityCannotAuthorizeDeletion()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = App(pg);
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var preview = await privacy.PreviewAsync(f.User, f.Old!.Value, 1, [f.Evidence], default);
        await db.MemoryOperations.Where(x => x.Id == preview.PreviewId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.PreviewExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Assert.That(async () =>
        {
            await privacy.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "expired", default);
        }).ThrowsExactly<DbUpdateConcurrencyException>();
        await Assert.That(async () =>
        {
            await privacy.EraseAsync(Guid.NewGuid(), preview.PreviewId, preview.ScopeHash, "foreign", default);
        }).ThrowsExactly<KeyNotFoundException>();
        var fresh = await privacy.PreviewAsync(f.User, f.Old.Value, 1, [f.Evidence], default);
        await db.Messages.Where(x => x.Id == f.Evidence)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "changed original"));
        await Assert.That(async () =>
        {
            await privacy.EraseAsync(f.User, fresh.PreviewId, fresh.ScopeHash, "changed", default);
        }).ThrowsExactly<DbUpdateConcurrencyException>();
    }

    [Test]
    [Arguments("quoted", false)]
    [Arguments("confirmation", true)]
    [Arguments("current", true)]
    [Arguments("independent", true)]
    [Arguments("uncertain", false)]
    public async Task OldQuotesAreNotNewEvidenceButCurrentConfirmationAndIndependentFactsAre(string kind, bool expected)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = App(pg);
        var f = await SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
            .ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var text = kind switch
        {
            "quoted" => "以前我说过“喜欢蓝色”。", "confirmation" => "以前我说过“喜欢蓝色”，现在仍然喜欢蓝色。",
            "independent" => "引用“喜欢蓝色”。我现在弹钢琴。", "uncertain" => "还是老样子吧（笑）", _ => "我现在喜欢蓝色。"
        };
        var message = new ChatMessage
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            SenderId = f.User, Role = MessageRole.User, Text = text, AllowReflection = true,
            ExternalMessageId = "fresh-" + kind,
            SegmentsJson = Json.Write(new[] { new MessageSegment(SegmentKind.ReplyReference, "source") }),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        var anchor = kind == "independent" ? "我现在弹钢琴" :
            kind == "current" ? "我现在喜欢蓝色" :
            kind == "confirmation" ? "现在仍然喜欢蓝色" : text;
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        var role = kind == "confirmation" ? "CurrentConfirmation" :
            kind == "uncertain" ? "Uncertain" :
            kind == "quoted" ? "HistoricalQuote" : "CurrentStatement";
        var assertion = new EvidenceAssertion(message.Id, role, start, anchor.Length, anchor,
            kind == "confirmation" ? start : null, kind == "confirmation" ? anchor : null);
        var candidate = new MemoryCandidate(kind == "independent" ? "piano" : "color", anchor, true, false,
            [message.Id], [assertion]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, candidate, default))
            .IsEqualTo(expected);
        var old = new MemoryCandidate("color", "喜欢蓝色", true, false, [f.Evidence]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, old, default)).IsFalse();
    }

    internal static TestApp App(TestPostgres pg)
    {
        return new TestApp(environment: "Production", postgresConnection: pg.ConnectionString);
    }

    internal static async Task<MemoryMaintenanceTests.Fixture> SeedAsync(TestApp app)
    {
        var f = await MemoryMaintenanceTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var original = await db.Messages.SingleAsync(x => x.Id == f.Evidence);
        original.Text = "喜欢蓝色，也弹钢琴。";
        original.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        var candidate = new MemoryCandidate("color", "喜欢蓝色", true, false, [original.Id]);
        var reflection = await db.Reflections.SingleAsync();
        reflection.Payload = Json.Write(new ReflectionProposal(false,
            new EmotionProposal(.1, 0, 0, "independent emotion", [original.Id]),
            [new RelationshipProposal("喜欢钢琴", true, [original.Id])], [candidate]));
        var memory = await db.Memories.SingleAsync(x => x.Id == f.Old);
        memory.Text = candidate.Text;
        memory.FactKey = candidate.FactKey;
        memory.EvidenceJson = Json.Write(candidate.EvidenceMessageIds);
        memory.SourceReflectionId = reflection.Id;
        var pending = await db.MemoryCandidates.SingleAsync();
        pending.Text = candidate.Text;
        pending.FactKey = candidate.FactKey;
        pending.CandidateHash = Json.Hash(Json.Write(candidate));
        pending.ResultMemoryId = memory.Id;
        pending.SourceReflectionId = reflection.Id;
        var relationship = new RelationshipState { BotId = f.Bot, UserId = f.User };
        db.Relationships.Add(relationship);
        db.RelationshipClaims.Add(new RelationshipClaim
        {
            RelationshipId = relationship.Id, BotId = f.Bot, SubjectId = f.User,
            Kind = "Preference", Text = "independent relationship", Origin = "Explicit", Qualification = "Current"
        });
        db.Notes.Add(new Note
            { BotId = f.Bot, UserId = f.User, ConversationId = f.Conversation, Content = "independent note" });
        db.Summaries.Add(new ConversationSummary
        {
            BotId = f.Bot, SessionId = f.Session, Revision = 1, Text = "original summary",
            MessageIdsJson = Json.Write(new[] { original.Id }),
            SourcesJson = Json.Write(new[] { ContentSources.Message(original) })
        });
        await db.SaveChangesAsync();
        return f;
    }
}