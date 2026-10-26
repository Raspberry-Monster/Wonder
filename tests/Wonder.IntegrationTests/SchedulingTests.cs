using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Wonder.Core;
using Wonder.Host;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class SchedulingTests
{
    [Test]
    [NotInParallel]
    public async Task SchedulerStartsWithAllInMemoryTriggersAfterEachRecreation()
    {
        using var app = new TestApp();
        using var scope = app.Services.CreateScope();
        for (var restart = 0; restart < 2; restart++)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>());
            services.AddWonderScheduling(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Chat:TimeZone"] = "Asia/Shanghai" }).Build());
            await using var provider = services.BuildServiceProvider();
            var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
            try
            {
                var metadata = await scheduler.GetMetadata();
                await Assert.That(metadata.JobStorePersistent).IsFalse();
                await Assert.That(metadata.JobStoreTypeName).Contains("Quartz.Impl.RAMJobStore");
                var triggers = await scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup());
                await Assert.That(triggers.Select(x => x.Name).ToArray()).IsEquivalentTo(new[]
                    { "sessions-interval", "retention-interval", "daily-cron", "daily-reconcile" });
                var daily = (ICronTrigger)(await scheduler.GetTrigger(new TriggerKey("daily-cron")))!;
                await Assert.That(daily.TimeZone.Id).IsEqualTo("Asia/Shanghai");
                await scheduler.Start();
                await Assert.That(await scheduler.GetStatus()).IsEqualTo(SchedulerStatus.Running);
            }
            finally
            {
                await scheduler.Shutdown(true);
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task WorkersDisabledDoesNotRegisterScheduler()
    {
        // Set before Program registers services; WAF's in-memory configuration is applied later.
        var previous = Environment.GetEnvironmentVariable("Workers__Enabled");
        try
        {
            Environment.SetEnvironmentVariable("Workers__Enabled", "false");
            using var app = new TestApp();
            await Assert.That(app.Services.GetService<ISchedulerFactory>()).IsNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("Workers__Enabled", previous);
        }
    }

    [Test]
    public async Task StartupSweepSchedulesFullGenerationAndOutboxSurvivesHostRecreation()
    {
        await using var pg = await TestPostgres.CreateAsync();
        Guid botId;
        Guid sessionId;
        Guid jobId;
        using (var app = pg.CreateApp())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
            var owner = new ApplicationUser { Id = Guid.NewGuid() };
            var bot = new BotInstance { OwnerId = owner.Id };
            var user = new LogicalUser { AllowMemory = true };
            var conversation = new Conversation { BotId = bot.Id, Kind = ConversationKind.Private };
            var session = new ConversationSession
            {
                BotId = bot.Id, ConversationId = conversation.Id, DeadlineAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            db.Users.Add(owner);
            db.Bots.Add(bot);
            db.LogicalUsers.Add(user);
            db.Conversations.Add(conversation);
            db.Sessions.Add(session);
            db.Messages.Add(new ChatMessage
            {
                BotId = bot.Id, ConversationId = conversation.Id, SessionId = session.Id, SenderId = user.Id,
                Text = "我喜欢蓝色", AllowReflection = true, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            });
            await db.SaveChangesAsync();
            var workflow = scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>();
            await workflow.SweepSessionsAsync(default);
            await workflow.ScheduleDailyAsync(default);
            var run = await db.ReflectionGenerations.AsNoTracking().SingleAsync();
            jobId = run.JobId;
            botId = bot.Id;
            sessionId = session.Id;
            await Assert.That(run.RawBoundary).IsGreaterThan(0);
        }

        using var restarted = pg.CreateApp();
        using var restartedScope = restarted.Services.CreateScope();
        var fresh = restartedScope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var maintenance = restartedScope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>();
        await maintenance.SweepSessionsAsync(default);
        await maintenance.ScheduleDailyAsync(default);
        await Assert.That(await fresh.Outbox.CountAsync(x => x.BotId == botId && x.Type == JobKind.Maintenance))
            .IsEqualTo(1);
        await Assert.That(await fresh.ReflectionGenerations.CountAsync(x => x.SessionId == sessionId)).IsEqualTo(1);
        var claim = await restartedScope.ServiceProvider.GetRequiredService<JobQueue>()
            .TryClaimAsync([JobKind.Reflection], default);
        await Assert.That(claim!.Id).IsEqualTo(jobId);
        // A real consumer resumes the durable payload after the old Host has been disposed.
        await fresh.LogicalUsers.ExecuteUpdateAsync(s => s.SetProperty(x => x.AllowMemory, false));
        await restartedScope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(claim, default);
        await Assert.That((await fresh.Outbox.AsNoTracking().SingleAsync(x => x.Id == jobId)).Status)
            .IsEqualTo(JobStatus.Completed);
        await Assert.That((await fresh.ReflectionGenerations.AsNoTracking().SingleAsync()).Status)
            .IsEqualTo(ReflectionGenerationStatus.Cancelled);
    }

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
        db.Users.Add(owner);
        db.Bots.Add(bot);
        var mood = new GlobalEmotionState
            { BotId = bot.Id, Valence = .8, LastEvolvedAt = DateTimeOffset.UtcNow.AddHours(-24) };
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
