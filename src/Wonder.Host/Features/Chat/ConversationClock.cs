using System.Globalization;
using Wonder.Core;

namespace Wonder.Host;

public sealed class ConversationClock(TimeProvider time, IConfiguration configuration)
{
    public TimeZoneInfo Zone { get; } =
        TimeZoneInfo.FindSystemTimeZoneById(configuration["Chat:TimeZone"] ?? "Asia/Shanghai");

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public DateTimeOffset Local(DateTimeOffset timestamp)
    {
        return TimeZoneInfo.ConvertTime(timestamp, Zone);
    }

    public string Context(DateTimeOffset currentMessageAt, DateTimeOffset? previousMessageAt, bool continued)
    {
        return Json.ForPrompt(new
        {
            timeZone = Zone.Id,
            currentMessageSentAt = Local(currentMessageAt).ToString("O", CultureInfo.InvariantCulture),
            previousMessageAt = previousMessageAt is { } previous
                ? Local(previous).ToString("O", CultureInfo.InvariantCulture)
                : null,
            continuingPreviousSession = continued
        });
    }
}
