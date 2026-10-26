using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class SimplificationUpgradeTests
{
    [Test]
    [Arguments("Fenced", false)]
    [Arguments("Completed", true)]
    public async Task UpgradeRetiresNarrativeAndLedgerAndRechecksOnlinePrivacyResults(string stage, bool existingJob)
    {
        await using var pg = await TestPostgres.CreateAsync("20261025183202_PrivacyEvidenceSpans");
        using var app = PrivacyTests.App(pg);
        var f = await PrivacyTests.SeedAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var privacy = scope.ServiceProvider.GetRequiredService<PrivacyWorkflow>();
        var operation = await privacy.ForgetAsync(f.User, f.Old!.Value, 1, "upgrade", default);
        await Assert.That(operation.Completed).IsTrue();
        await db.MemoryOperations.Where(x => x.Id == operation.OperationId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Stage, stage));
        // An old completion marker must not hide a restored producer payload.
        await db.MemoryCandidates.ExecuteUpdateAsync(s => s.SetProperty(x => x.Text, "restored draft"));
        if (!existingJob)
            await db.Outbox.Where(x => x.Type == JobKind.MemoryPrivacy).ExecuteDeleteAsync();
        else
        {
            var oldJob = await db.Outbox.SingleAsync(x => x.Type == JobKind.MemoryPrivacy);
            Jobs.Complete(db, oldJob);
        }
        db.Models.Add(new ModelProfile { BotId = f.Bot, TaskKind = TaskKind.DailyNarrative });
        db.Usage.Add(new UsageRecord { BotId = f.Bot, TaskKind = TaskKind.DailyNarrative, InputTokens = 17 });
        var retired = new OutboxMessage { BotId = f.Bot, Type = JobKind.Narrative, Status = JobStatus.Running,
            ProgressJson = "old narrative draft", LeaseId = Guid.NewGuid(), LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5) };
        db.Outbox.Add(retired);
        db.Outbox.Add(new OutboxMessage { BotId = f.Bot, Type = JobKind.Ledger, ProgressJson = "old export" });
        await db.SaveChangesAsync();
        var narrativeId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Narratives" ("Id", "BotId", "Date", "StateRevision", "Text", "GeneratedAt")
            VALUES ({narrativeId}, {f.Bot}, {DateOnly.FromDateTime(DateTime.UtcNow)}, {1L}, {"old narrative"}, {DateTimeOffset.UtcNow})
            """);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        await Assert.That(await db.Models.AnyAsync(x => x.TaskKind == TaskKind.DailyNarrative)).IsFalse();
        await Assert.That((await db.Usage.SingleAsync(x => x.TaskKind == TaskKind.DailyNarrative)).InputTokens).IsEqualTo(17);
        var cancelled = await db.Outbox.Where(x => x.Type == JobKind.Narrative || x.Type == JobKind.Ledger).ToArrayAsync();
        await Assert.That(cancelled.All(x => x.Status == JobStatus.Cancelled && x.ProgressJson == null && x.LeaseId == null)).IsTrue();
        var pending = await privacy.GetAsync(f.User, operation.OperationId, default);
        await Assert.That(pending.Stage).IsEqualTo("PendingCleanup");
        await Assert.That(pending.Completed).IsFalse();
        var job = await db.Outbox.AsNoTracking().SingleAsync(x => x.Type == JobKind.MemoryPrivacy);
        await Assert.That(job.Status).IsEqualTo(JobStatus.Pending);
        await Assert.That(await db.Inbox.AnyAsync(x => x.EventId == job.Id)).IsFalse();
        await privacy.RunAsync(job.Id, default);
        await Assert.That((await privacy.GetAsync(f.User, operation.OperationId, default)).Completed).IsTrue();
        await Assert.That((await db.MemoryCandidates.AsNoTracking().SingleAsync()).Text).IsEmpty();
        await Assert.That(await db.Revocations.AnyAsync()).IsTrue();
        await Assert.That(app.ModelRequests).IsEmpty();
        await Assert.That(await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND (table_name = 'Narratives'
                OR (table_name = 'MemoryOperations' AND column_name IN ('RecoveryVerified', 'ProtectionReceiptJson')))
            """).SingleAsync()).IsEqualTo(0);
    }
}
