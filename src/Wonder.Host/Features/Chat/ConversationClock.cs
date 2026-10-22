using Wonder.Infrastructure;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Wonder.Core;

namespace Wonder.Host;

public sealed class ConversationClock(TimeProvider time, IConfiguration configuration)
{
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(configuration["Chat:TimeZone"] ?? "Asia/Shanghai");
    public DateTimeOffset UtcNow => time.GetUtcNow();
    public DateTimeOffset Local(DateTimeOffset timestamp) => TimeZoneInfo.ConvertTime(timestamp, Zone);

    public string Context(DateTimeOffset currentMessageAt, DateTimeOffset? previousMessageAt, bool continued)
    {
        var now = UtcNow;
        return Json.Write(new
        {
            currentTime = Local(now).ToString("O", CultureInfo.InvariantCulture),
            utcTime = now.ToString("O", CultureInfo.InvariantCulture),
            timeZone = Zone.Id,
            weekday = Local(now).DayOfWeek.ToString(),
            currentMessageSentAt = Local(currentMessageAt).ToString("O", CultureInfo.InvariantCulture),
            previousMessageAt = previousMessageAt is { } previous ? Local(previous).ToString("O", CultureInfo.InvariantCulture) : null,
            elapsedSeconds = previousMessageAt is { } at ? (long?)Math.Max(0, (now - at).TotalSeconds) : null,
            continuingPreviousSession = continued
        });
    }
}
