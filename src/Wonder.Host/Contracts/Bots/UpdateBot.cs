namespace Wonder.Host.Contracts;

public sealed record UpdateBot(
    string? Name,
    bool? Enabled,
    bool? PersistMessages,
    int? MessageRetentionDays,
    int? PrivateIdleMinutes,
    int? GroupIdleMinutes,
    long? DailyTokenLimit,
    long? MonthlyTokenLimit);