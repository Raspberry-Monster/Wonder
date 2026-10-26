namespace Wonder.Host.Contracts;

public sealed record ModelRouteResponse(
    Guid Id,
    ModelTaskKind TaskKind,
    string Endpoint,
    string Model,
    int ContextWindow,
    int MaxOutputTokens,
    bool SupportsImages,
    bool SupportsJson,
    bool SupportsTools,
    bool? ThinkingEnabled,
    string? ReasoningEffort,
    int EmbeddingDimensions,
    decimal InputCostPerMillion,
    decimal OutputCostPerMillion,
    string TokenizerId,
    int MessageTokenOverhead,
    int ImageTokenBudget,
    bool HasApiKey,
    string ResolvedTokenizer);