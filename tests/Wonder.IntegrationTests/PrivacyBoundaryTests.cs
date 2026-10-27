using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Core.Exceptions;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class PrivacyBoundaryTests
{
    [Test]
    [Arguments("Current")]
    [Arguments("UnprovenChangedAssertion")]
    public async Task ProducerCleanupMatchesCurrentAssertionsAndNeverCompletesUnprovenPayloads(string shape)
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var reflection = await db.Reflections.SingleAsync();
        var node = JsonNode.Parse(reflection.Payload)!;
        var assertion = node["memories"]![0]!;
        if (shape == "UnprovenChangedAssertion") assertion["factKey"] = "unknown-renamed-key";
        reflection.Payload = node.ToJsonString(Json.Options);
        await db.SaveChangesAsync();
        var result = await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
            .ForgetAsync(f.User, f.Old!.Value, 1, "shapes", default);
        var current = await db.Reflections.AsNoTracking().SingleAsync();
        await Assert.That(Json.Read<ReflectionProposal>(current.Payload).Emotion!.Reason)
            .IsEqualTo("independent emotion");
        await Assert.That(result.Completed).IsEqualTo(shape != "UnprovenChangedAssertion");
        await Assert.That(Json.Read<ReflectionProposal>(current.Payload).Memories.Length)
            .IsEqualTo(shape == "UnprovenChangedAssertion" ? 1 : 0);
        if (shape == "UnprovenChangedAssertion")
        {
            await Assert.That(current.MemorySourcesBlocked).IsTrue();
            await Assert.That(result.Stage).IsEqualTo("NeedsReview");
        }
    }

    [Test]
    public async Task ReflectionRechecksInFlightDeletionAndOnlyReceivesMaskedRetainedEvidenceAfterward()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        db.Models.Add(new ModelProfile
        {
            BotId = f.Bot, TaskKind = TaskKind.LightReflection, Endpoint = "https://model.example/v1/",
            Model = "light",
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-only"),
            SupportsJson = true
        });
        var source = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == f.Evidence);
        var first = Jobs.Add(db, f.Bot, JobKind.Reflection,
            new ReflectionJob(f.Session, f.User, source.Sequence, 0, ReflectionKind.Light), f.Conversation);
        await db.SaveChangesAsync();
        var oldProposal = new ReflectionProposal(false, null, [],
            [new MemoryCandidate("color", "喜欢蓝色", true, false, [source.Id])]);

        string Response(ReflectionProposal proposal)
        {
            return Json.Write(new
            {
                choices = new[] { new { message = new { content = Json.Write(proposal) } } },
                usage = new { prompt_tokens = 1, completion_tokens = 1 }
            });
        }

        app.ModelResponses.Enqueue(Response(oldProposal));
        app.BeforeModelResponse = async _ =>
        {
            app.BeforeModelResponse = null;
            using var deletionScope = app.Services.CreateScope();
            await deletionScope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
                .ForgetAsync(f.User, f.Old!.Value, 1, "in-flight", default);
        };
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().GenerateAsync(first.Id, default);
        // Inspect all newly produced results rather than relying on a producer ID inferred from a Session.
        var results = await db.Reflections.AsNoTracking().ToListAsync();
        await Assert.That(results.Select(x => Json.Read<ReflectionProposal>(x.Payload).Memories.Length).ToArray())
            .IsEquivalentTo(new[] { 0, 0 });
        var later = new ChatMessage
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session, SenderId = f.User,
            Role = MessageRole.User, AllowReflection = true, ExternalMessageId = "later", Text = "今天练琴",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        db.Messages.Add(later);
        await db.SaveChangesAsync();
        var (run, _) = await ReflectionRuns.AdmitAsync(db, f.Bot, f.Conversation,
            new ReflectionJob(f.Session, f.User, later.Sequence, 0, ReflectionKind.Full), default);
        await db.SaveChangesAsync();
        app.ModelResponses.Enqueue(Response(new ReflectionProposal(false, null, [], [])));
        await scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>().GenerateAsync(run!.JobId, default);
        var wire = JsonNode.Parse(app.ModelRequests.Last())!;
        var statements = JsonNode.Parse(wire["messages"]![1]!["content"]!.GetValue<string>())!;
        var text = statements[0]!["text"]!.GetValue<string>();
        await Assert.That(text).DoesNotContain("喜欢蓝色");
        await Assert.That(text).Contains("弹钢琴");
    }

    [Test]
    public async Task PrivacyHttpRequiresCurrentIdentityVersionKeyAndProtectsForeignReceipts()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var self = app.CreateClient();
        using var stranger = app.CreateClient();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var other = new LogicalUser { AllowMemory = true };
        db.LogicalUsers.Add(other);
        await db.SaveChangesAsync();
        foreach (var entry in new[]
                     { (self, "self@example.test", f.User), (stranger, "stranger@example.test", other.Id) })
        {
            entry.Item1.DefaultRequestHeaders.Add("X-Wonder", "1");
            var login = new { email = entry.Item2, password = "A-strong-test-password!" };
            await entry.Item1.PostAsJsonAsync("/api/v1/auth/register", login);
            await entry.Item1.PostAsJsonAsync("/api/v1/auth/login", login);
            (await db.Users.SingleAsync(x => x.Email == entry.Item2)).LogicalUserId = entry.Item3;
            await db.SaveChangesAsync();
        }

        var path = "/api/v1/me/memories/" + f.Old;
        await Assert.That((await self.DeleteAsync(path)).StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        self.DefaultRequestHeaders.Add("Idempotency-Key", "http-forget");
        stranger.DefaultRequestHeaders.Add("Idempotency-Key", "foreign");
        var response = await self.DeleteAsync(path + "?expectedRevision=1");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var receipt = (await response.Content.ReadFromJsonAsync<PrivacyOperationView>(Json.Options))!;
        await Assert.That(receipt.Completed).IsTrue();
        var again = await self.DeleteAsync(path + "?expectedRevision=1");
        await Assert.That((await again.Content.ReadFromJsonAsync<PrivacyOperationView>(Json.Options))!.OperationId)
            .IsEqualTo(receipt.OperationId);
        await Assert.That((await self.DeleteAsync(path + "?expectedRevision=2")).StatusCode)
            .IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await stranger.DeleteAsync(path + "?expectedRevision=1")).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await stranger.GetAsync("/api/v1/me/memory-operations/" + receipt.OperationId)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
        await Assert
            .That((await stranger.PostAsync("/api/v1/me/memory-operations/" + receipt.OperationId + "/retry", null))
                .StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MediaResumeFindsAnOrphanFileFromFrozenMetadataAndRefusesChangedContent()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var root = scope.ServiceProvider.GetRequiredService<MediaStore>().Root;
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("private file");
        await File.WriteAllBytesAsync(path, bytes);
        var media = new MediaAsset
        {
            BotId = f.Bot, MessageId = f.Evidence, Path = path, Hash = Json.Hash("https://cache-key"),
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes))
        };
        db.Media.Add(media);
        await db.SaveChangesAsync();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var preview = await privacy.PreviewAsync(f.User, f.Old!.Value, 1, [f.Evidence], default);
        // Changing the file after preview forces an online cleanup failure, independent of recovery services.
        await File.WriteAllTextAsync(path, "changed file owned by something else");
        var pending = await privacy.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "file", default);
        await db.Media.Where(x => x.Id == media.Id)
            .ExecuteDeleteAsync(); // A row may disappear while a failed physical cleanup leaves its file.
        var job = await db.Outbox.AsNoTracking().SingleAsync(x => x.Type == JobKind.MemoryPrivacy);
        await privacy.RunAsync(job.Id, default);
        var partial = await privacy.GetAsync(f.User, pending.OperationId, default);
        await Assert.That(partial.Items.Single(x => x.Kind == "Media").ErrorCode).IsEqualTo("MediaContentChanged");
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(partial.Completed).IsFalse();
        await File.WriteAllBytesAsync(path, bytes);
        await privacy.RetryAsync(f.User, pending.OperationId, default);
        var retry = await db.Outbox.AsNoTracking().Where(x => x.Type == JobKind.MemoryPrivacy)
            .OrderByDescending(x => x.Sequence).FirstAsync();
        await privacy.RunAsync(retry.Id, default);
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That((await privacy.GetAsync(f.User, pending.OperationId, default)).Completed).IsTrue();
    }

    [Test]
    public async Task RetainedMixedEvidenceOnlyExposesProvenUnrelatedSpans()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
            .ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var safe = await SafeReflectionEvidence.ReadAsync(db, f.Bot, f.User, f.Session, 0, long.MaxValue, 0, false,
            default);
        await Assert.That(safe.Rows.Single().Text).DoesNotContain("喜欢蓝色");
        await Assert.That(safe.Rows.Single().Text).Contains("也弹钢琴");
        var row = await db.Messages.AsNoTracking().SingleAsync(x => x.Id == f.Evidence);
        var start = row.Text.IndexOf("弹钢琴", StringComparison.Ordinal);
        var independent = new MemoryCandidate("piano", "弹钢琴", true, false, [row.Id],
            [new EvidenceAssertion(row.Id, "CurrentStatement", start, 3, "弹钢琴")]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, independent, default)).IsTrue();
        var renamedOldFact = new MemoryCandidate("renamed-key", "喜欢蓝色", true, false, [row.Id],
            [new EvidenceAssertion(row.Id, "CurrentStatement", 0, 4, "喜欢蓝色")]);
        await Assert.That(await EvidencePolicy.MayAdmitAsync(db, f.Bot, f.User, renamedOldFact, default)).IsFalse();
        await db.EvidenceSuppressions.ExecuteUpdateAsync(s => s.SetProperty(x => x.SpansJson, "[]"));
        await Assert
            .That((await SafeReflectionEvidence.ReadAsync(db, f.Bot, f.User, f.Session, 0, long.MaxValue, 0, false,
                default)).Rows).IsEmpty();
    }

    [Test]
    public async Task EraseCanFollowForgetUsingTheRetainedAuthorizedEvidenceScope()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var forgotten = await privacy.ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var preview = await privacy.PreviewAsync(f.User, forgotten.TargetMemoryId, forgotten.TargetRevision,
            [f.Evidence], default);
        await Assert.That(preview.Messages.Single().Id).IsEqualTo(f.Evidence);
        var erased = await privacy.EraseAsync(f.User, preview.PreviewId, preview.ScopeHash, "erase-later", default);
        await Assert.That(erased.Completed).IsTrue();
        await Assert
            .That((await scope.ServiceProvider.GetRequiredService<WonderDbContext>().Messages.AsNoTracking()
                .SingleAsync(x => x.Id == f.Evidence)).Text).IsEqualTo("");
    }

    [Test]
    public async Task UnknownProducerIsFencedWithoutClearingIndependentReflectionChildren()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await db.MemoryCandidates.ExecuteDeleteAsync();
        var result = await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
            .ForgetAsync(f.User, f.Old!.Value, 1, "unknown", default);
        await Assert.That(result.Stage).IsEqualTo("NeedsReview");
        await Assert.That(result.Completed).IsFalse();
        var reflection = await db.Reflections.AsNoTracking().SingleAsync();
        await Assert.That(reflection.MemorySourcesBlocked).IsTrue();
        await Assert.That(Json.Read<ReflectionProposal>(reflection.Payload).Emotion!.Reason)
            .IsEqualTo("independent emotion");
    }

    [Test]
    public async Task IndependentBoundariesSurviveWhileInferredClaimsLoseQualification()
    {
        await using var pg = await TestPostgres.CreateAsync();
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var state = await db.Relationships.SingleAsync();
        foreach (var origin in new[] { "Explicit", "Unknown", "Inferred" })
            db.RelationshipClaims.Add(new RelationshipClaim
            {
                RelationshipId = state.Id, BotId = f.Bot, SubjectId = f.User,
                Kind = "Boundary", Text = origin + " boundary", Origin = origin, Qualification = "Current",
                MemorySourcesJson = Json.Write(new[] { f.Old!.Value }), EvidenceJson = Json.Write(new[] { f.Evidence })
            });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
            .ForgetAsync(f.User, f.Old!.Value, 1, "forget", default);
        var claims = await db.RelationshipClaims.AsNoTracking().ToListAsync();
        await Assert.That(claims.Single(x => x.Origin == "Inferred").Qualification).IsEqualTo("Tentative");
        await Assert.That(claims.Where(x => x.Origin != "Inferred").All(x => x.Qualification == "Current")).IsTrue();
        var context = await RelationshipClaims.ForModelAsync(db, state, default);
        await Assert.That(context).Contains("Explicit boundary");
        await Assert.That(context).Contains("Unknown boundary");
        await Assert.That(context).DoesNotContain("Inferred boundary");
    }

    [Test]
    [Arguments(false, false, "Memory")]
    [Arguments(true, false, "Memory")]
    [Arguments(true, true, "Memory")]
    [Arguments(false, false, "Summary")]
    [Arguments(true, false, "Relationship")]
    public async Task OutgoingAndForgetUseTheSamePgFenceAndOnlyStopTheAffectedRound(bool sendFirst,
        bool deliveryUnknown, string sourceKind)
    {
        await using var pg = await TestPostgres.CreateAsync();
        var barrier = new FenceBarrier();
        using var app = new TestApp(environment: "MemoryLifecycleTest", postgresConnection: pg.ConnectionString,
            interceptor: barrier);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        db.Bindings.Add(new PlatformBinding { BotId = f.Bot, Enabled = true });
        var memory = await db.Memories.AsNoTracking().SingleAsync(x => x.Id == f.Old);
        var secondMemory = new Memory
            { BotId = f.Bot, SubjectId = f.User, Text = "independent current memory", FactKey = "independent" };
        db.Memories.Add(secondMemory);
        var snapshot = new ContextSnapshot(f.Bot, f.Session, 1, 1, 0, 0, [memory.Id, secondMemory.Id], "race",
            SourceUserIds: [f.User], Sources: [ContentSources.Memory(memory), ContentSources.Memory(secondMemory)]);
        if (sourceKind == "Summary")
        {
            var summary = await db.Summaries.SingleAsync();
            summary.SourcesJson = Json.Write(new[] { ContentSources.Memory(memory) });
            snapshot = snapshot with
            {
                RetrievedMemoryIds = [], SourceUserIds = [], Sources = [ContentSources.Summary(summary)]
            };
        }

        if (sourceKind == "Relationship")
        {
            var relationship = await db.Relationships.SingleAsync();
            db.RelationshipClaims.Add(new RelationshipClaim
            {
                RelationshipId = relationship.Id, BotId = f.Bot, SubjectId = f.User,
                Text = "dependent relationship reference", Origin = "Inferred", Qualification = "Current",
                MemorySourcesJson = Json.Write(new[] { memory.Id })
            });
            await db.SaveChangesAsync();
            snapshot = snapshot with
            {
                RetrievedMemoryIds = [], SourceUserIds = [],
                Sources = [(await RelationshipClaims.ProjectionAsync(db, relationship, default)).Source]
            };
        }

        var generatedJob = Jobs.Add(db, f.Bot, JobKind.Chat, new IdPayload(f.Evidence), f.Conversation);
        var generated = new ChatMessage
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            Role = MessageRole.Assistant, Text = "generated", ExternalMessageId = "generated:" + generatedJob.Id,
            ContextSnapshotJson = Json.Write(snapshot), ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        var batch = new OutgoingMessageBatch
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            SourceJobId = generatedJob.Id, SourceSnapshotJson = generated.ContextSnapshotJson
        };
        db.Messages.Add(generated);
        db.Batches.Add(batch);
        var parts = Enumerable.Range(0, 3).Select(i => new OutgoingMessagePart
            { BatchId = batch.Id, Sequence = i, Text = "part-" + i }).ToArray();
        db.Parts.AddRange(parts);
        var outgoing = Jobs.Add(db, f.Bot, JobKind.Outgoing, new IdPayload(batch.Id), f.Conversation);
        var unrelated = new OutgoingMessageBatch
        {
            BotId = f.Bot, ConversationId = f.Conversation, SessionId = f.Session,
            SourceJobId = Guid.NewGuid(),
            SourceSnapshotJson = Json.Write(snapshot with
            {
                RetrievedMemoryIds = [secondMemory.Id], Sources = [ContentSources.Memory(secondMemory)]
            })
        };
        db.Batches.Add(unrelated);
        db.Parts.Add(new OutgoingMessagePart { BatchId = unrelated.Id, Text = "unrelated" });
        var independent = Jobs.Add(db, f.Bot, JobKind.Outgoing, new IdPayload(unrelated.Id), f.Conversation);
        await db.SaveChangesAsync();

        async Task Forget()
        {
            using var another = app.Services.CreateScope();
            await another.ServiceProvider.GetRequiredService<PrivacyWorkflow>()
                .ForgetAsync(f.User, memory.Id, 1, "race", default);
        }

        if (sendFirst)
        {
            app.Sender.BeforeSend = async text =>
            {
                if (text == "part-0") await Forget();
            };
            app.Sender.Unknown = deliveryUnknown;
            if (deliveryUnknown)
                await Assert.That(() =>
                        scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default))
                    .ThrowsExactly<DeliveryUnknownException>();
            else await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
            app.Sender.Unknown = false;
        }
        else
        {
            barrier.Armed = true;
            // Pause the real Forget transaction after acquiring its actor row lock.
            var forget = Task.Run(async () =>
            {
                barrier.Deletion.Value = true;
                await Forget();
            });
            await barrier.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var send = Task.Run(async () =>
            {
                barrier.Outgoing.Value = true;
                using var s = app.Services.CreateScope();
                await s.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(outgoing.Id, default);
            });
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            barrier.Release.TrySetResult();
            await Task.WhenAll(forget, send).WaitAsync(TimeSpan.FromSeconds(15));
        }

        await scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>().RunAsync(independent.Id, default);
        var actual = await db.Parts.AsNoTracking().Where(x => x.BatchId == batch.Id).OrderBy(x => x.Sequence)
            .ToListAsync();
        await Assert.That(actual[0].Status).IsEqualTo(deliveryUnknown ? DeliveryStatus.DeliveryUnknown :
            sendFirst ? DeliveryStatus.Sent : DeliveryStatus.Failed);
        await Assert.That(actual.Skip(1).All(x => x.Status == DeliveryStatus.Failed)).IsTrue();
        await Assert.That(app.Sender.Sent)
            .IsEquivalentTo(sendFirst && !deliveryUnknown ? new[] { "part-0", "unrelated" } : new[] { "unrelated" });
    }

    private sealed class FenceBarrier : DbCommandInterceptor
    {
        public AsyncLocal<bool> Outgoing { get; } = new();
        public AsyncLocal<bool> Deletion { get; } = new();
        public bool Armed { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && Outgoing.Value && command.CommandText.Contains("LogicalUsers", StringComparison.Ordinal) &&
                command.CommandText.Contains("RevocationEpoch", StringComparison.Ordinal)) Reached.TrySetResult();
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Armed && Deletion.Value && !Held.Task.IsCompleted &&
                command.CommandText.Contains("LogicalUsers", StringComparison.Ordinal) &&
                command.CommandText.Contains("RevocationEpoch", StringComparison.Ordinal))
            {
                Held.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }

            return result;
        }
    }
}