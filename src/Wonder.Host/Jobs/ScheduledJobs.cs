using Quartz;

namespace Wonder.Host;

[DisallowConcurrentExecution]
public sealed class SessionLifecycleJob(MaintenanceWorkflow workflow) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return new ValueTask(workflow.SweepSessionsAsync(cancellationToken));
    }
}

[DisallowConcurrentExecution]
public sealed class RetentionCleanupJob(MaintenanceWorkflow workflow) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return new ValueTask(workflow.CleanupAsync(cancellationToken));
    }
}

[DisallowConcurrentExecution]
public sealed class DailyMaintenanceJob(MaintenanceWorkflow workflow) : IJob
{
    public ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        return new ValueTask(workflow.ScheduleDailyAsync(cancellationToken));
    }
}

public static class Scheduling
{
    public static IServiceCollection AddWonderScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Chat:TimeZone"] ?? "Asia/Shanghai");
        services.AddQuartz(q =>
        {
            q.ConfigureScheduler(options =>
            {
                options.InstanceName = "Wonder";
                options.GenerateInstanceId = true;
            });
            q.UseInMemoryStore();
            q.AddJob<SessionLifecycleJob>(j => j.WithIdentity("sessions"));
            q.AddTrigger(t => t.WithIdentity("sessions-interval").ForJob("sessions").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithInterval(TimeSpan.FromSeconds(15)).RepeatForever()
                        .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithExistingCount)));
            q.AddJob<RetentionCleanupJob>(j => j.WithIdentity("retention"));
            q.AddTrigger(t => t.WithIdentity("retention-interval").ForJob("retention").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithInterval(TimeSpan.FromMinutes(5)).RepeatForever()
                        .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithExistingCount)));
            q.AddJob<DailyMaintenanceJob>(j => j.WithIdentity("daily"));
            q.AddTrigger(t => t.WithIdentity("daily-cron").ForJob("daily")
                .WithCronSchedule("0 0 0 * * ?",
                    s => s.InTimeZone(zone).WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed)));
            // Reconcile today's work after startup and when a Bot is enabled during the day.
            q.AddTrigger(t => t.WithIdentity("daily-reconcile").ForJob("daily").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithInterval(TimeSpan.FromMinutes(5)).RepeatForever()
                        .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithExistingCount)));
        });
        return services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    }
}
