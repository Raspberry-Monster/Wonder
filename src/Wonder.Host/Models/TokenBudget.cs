using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public sealed class TokenBudget(IDbContextFactory<WonderDbContext> factory)
{
    public async Task<Guid> ReserveAsync(ModelProfile profile, long tokens, CancellationToken ct)
    {
        // Only reservation creation is transactional; the network call never holds a database lock.
        for (var attempt = 0;; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                var month = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero);
                var bot = await db.Bots.AsNoTracking().SingleAsync(x => x.Id == profile.BotId, ct);
                var active = db.Usage.Where(x => x.BotId == bot.Id &&
                                                 (x.ReservationExpiresAt == null || x.ReservationExpiresAt > now));
                var monthly = await active.Where(x => x.ReservationExpiresAt != null || x.CreatedAt >= month)
                    .SumAsync(x => x.InputTokens + x.OutputTokens, ct);
                var daily = await active.Where(x => x.ReservationExpiresAt != null || x.CreatedAt >= today)
                    .SumAsync(x => x.InputTokens + x.OutputTokens, ct);
                if (tokens > bot.DailyTokenLimit || tokens > bot.MonthlyTokenLimit)
                    throw new ContextLimitException(
                        "This request alone exceeds the bot token quota. Increase the quota or reduce the request.");
                if (monthly + tokens > bot.MonthlyTokenLimit)
                    throw new TokenQuotaException(month.AddMonths(1), "Monthly token quota exhausted (UTC).");
                if (daily + tokens > bot.DailyTokenLimit)
                    throw new TokenQuotaException(today.AddDays(1), "Daily token quota exhausted (UTC).");
                var record = new UsageRecord
                {
                    BotId = bot.Id, ModelProfileId = profile.Id, TaskKind = profile.TaskKind,
                    InputTokens = tokens, ReservationExpiresAt = now.AddMinutes(15)
                };
                db.Usage.Add(record);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return record.Id;
            }
            catch (Exception ex) when (attempt < 4 && ex.GetBaseException() is PostgresException { SqlState: "40001" })
            {
                // Let the competing reservation commit before reading a fresh serializable snapshot.
                await Task.Delay(Random.Shared.Next(10, 30) * (attempt + 1), ct);
            }
        }
    }

    public async Task SettleAsync(Guid reservation, ModelProfile profile, long input, long output, CancellationToken ct,
        bool estimated = false)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var record = await db.Usage.SingleAsync(x => x.Id == reservation, ct);
        record.InputTokens = input;
        record.OutputTokens = output;
        record.Estimated = estimated;
        record.Cost = (input * profile.InputCostPerMillion + output * profile.OutputCostPerMillion) / 1_000_000m;
        record.ReservationExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(Guid reservation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var db = await factory.CreateDbContextAsync(timeout.Token);
        await db.Usage.Where(x => x.Id == reservation && x.ReservationExpiresAt != null)
            .ExecuteDeleteAsync(timeout.Token);
    }
}