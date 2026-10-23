using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed record BotResponse(
    Guid Id,
    string Name,
    bool Enabled,
    long SettingsRevision,
    int MessageRetentionDays,
    int PrivateIdleMinutes,
    int GroupIdleMinutes,
    bool PersistMessages,
    long DailyTokenLimit,
    long MonthlyTokenLimit)
{
    public static BotResponse From(BotInstance bot)
    {
        return new BotResponse(bot.Id, bot.Name, bot.Enabled, bot.SettingsRevision,
            bot.MessageRetentionDays, bot.PrivateIdleMinutes, bot.GroupIdleMinutes, bot.PersistMessages,
            bot.DailyTokenLimit, bot.MonthlyTokenLimit);
    }
}