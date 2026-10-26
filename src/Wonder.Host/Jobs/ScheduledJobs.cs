using Quartz;

namespace Wonder.Host;

[DisallowConcurrentExecution]
public sealed class SessionLifecycleJob(MaintenanceWorkflow workflow) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        return workflow.SweepSessionsAsync(context.CancellationToken);
    }
}

[DisallowConcurrentExecution]
public sealed class RetentionCleanupJob(MaintenanceWorkflow workflow) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        return workflow.CleanupAsync(context.CancellationToken);
    }
}

[DisallowConcurrentExecution]
public sealed class DailyMaintenanceJob(MaintenanceWorkflow workflow) : IJob
{
    public Task Execute(IJobExecutionContext context)
    {
        return workflow.ScheduleDailyAsync(context.CancellationToken);
    }
}

public static class Scheduling
{
    public static IServiceCollection AddWonderScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration["Chat:TimeZone"] ?? "Asia/Shanghai");
        services.AddQuartz(q =>
        {
            q.SchedulerId = "AUTO";
            q.SchedulerName = "Wonder";
            q.UseInMemoryStore();
            q.AddJob<SessionLifecycleJob>(j => j.WithIdentity("sessions"));
            q.AddTrigger(t => t.WithIdentity("sessions-interval").ForJob("sessions").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithIntervalInSeconds(15).RepeatForever().WithMisfireHandlingInstructionNextWithExistingCount()));
            q.AddJob<RetentionCleanupJob>(j => j.WithIdentity("retention"));
            q.AddTrigger(t => t.WithIdentity("retention-interval").ForJob("retention").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithIntervalInMinutes(5).RepeatForever().WithMisfireHandlingInstructionNextWithExistingCount()));
            q.AddJob<DailyMaintenanceJob>(j => j.WithIdentity("daily"));
            q.AddTrigger(t => t.WithIdentity("daily-cron").ForJob("daily")
                .WithCronSchedule("0 0 0 * * ?",
                    s => s.InTimeZone(zone).WithMisfireHandlingInstructionFireAndProceed()));
            // Reconcile today's work after startup and when a Bot is enabled during the day.
            q.AddTrigger(t => t.WithIdentity("daily-reconcile").ForJob("daily").StartNow()
                .WithSimpleSchedule(s =>
                    s.WithIntervalInMinutes(5).RepeatForever().WithMisfireHandlingInstructionNextWithExistingCount()));
        });
        return services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
    }
}