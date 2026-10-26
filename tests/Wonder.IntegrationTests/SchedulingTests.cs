using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class SchedulingTests
{
    [Test]
    public async Task DailyReconciliationUsesLocalDateAndDoesNotDuplicateAfterRestart()
    {
        var clock = new Clock();
        using var app = new TestApp(clock);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        await db.SaveChangesAsync();
        var workflow = scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>();
        await workflow.ScheduleDailyAsync(default);
        await workflow.ScheduleDailyAsync(default);
        var first = await db.Outbox.AsNoTracking().SingleAsync();
        await Assert.That(Json.Read<DailyJob>(first.Payload).Date).IsEqualTo(new DateOnly(2026, 10, 22));
        clock.Now = clock.Now.AddDays(3);
        await workflow.ScheduleDailyAsync(default);
        var jobs = await db.Outbox.AsNoTracking().OrderBy(x => x.ScheduledDate).ToListAsync();
        await Assert.That(jobs.Count).IsEqualTo(2);
        await Assert.That(jobs[1].ScheduledDate).IsEqualTo(new DateOnly(2026, 10, 25));
    }

    [Test]
    public async Task DailyMaintenanceEvolvesMoodWithoutSchedulingGeneration()
    {
        using var app = new TestApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        db.Users.Add(owner); db.Bots.Add(bot);
        var mood = new GlobalEmotionState { BotId = bot.Id, Valence = .8, LastEvolvedAt = DateTimeOffset.UtcNow.AddHours(-24) };
        db.Emotions.Add(mood);
        var job = Jobs.Add(db, bot.Id, JobKind.Maintenance, new DailyJob(DateOnly.FromDateTime(DateTime.UtcNow)));
        await db.SaveChangesAsync();
        var revision = mood.Revision;
        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().RunAsync(job.Id, default);
        db.ChangeTracker.Clear();
        await Assert.That((await db.Emotions.SingleAsync()).Revision).IsEqualTo(revision + 1);
        await Assert.That((await db.Emotions.SingleAsync()).Valence).IsLessThan(.8);
        await Assert.That((await db.Outbox.SingleAsync()).Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(app.ModelRequests).IsEmpty();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-21T16:05:00Z");

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}