using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed record CreateBot(string Name, string Identity);

public sealed record UpdateBot(
    string? Name,
    bool? Enabled,
    bool? PersistMessages,
    int? MessageRetentionDays,
    int? PrivateIdleMinutes,
    int? GroupIdleMinutes,
    long? DailyTokenLimit,
    long? MonthlyTokenLimit);

public sealed record PublishIdentity(string Content);

public sealed record BindingInput(
    string PlatformAccountId,
    string WebSocketUrl,
    string? AccessToken,
    bool Enabled = true);

public sealed record ModelRouteInput(
    TaskKind TaskKind,
    string Endpoint,
    string Model,
    string? ApiKey,
    int ContextWindow = 8192,
    int MaxOutputTokens = 1024,
    bool SupportsImages = false,
    bool SupportsJson = false,
    int EmbeddingDimensions = 1536,
    decimal InputCostPerMillion = 0,
    decimal OutputCostPerMillion = 0,
    bool? ThinkingEnabled = null,
    string? ReasoningEffort = null);

public sealed record GroupPolicyInput(
    bool RespondToMention = true,
    bool RespondToReply = true,
    string CommandPrefix = "/wonder",
    bool PersistMessages = true,
    bool AllowReflection = true,
    bool ProactiveEnabled = false,
    Guid? ContextSpaceId = null);

public sealed record DeliveryResolution(string Action, string? ExternalId);