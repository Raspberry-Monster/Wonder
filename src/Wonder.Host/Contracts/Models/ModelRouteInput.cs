using Wonder.Core;

namespace Wonder.Host.Contracts;

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
    string? ReasoningEffort = null,
    bool? SupportsTools = null,
    string TokenizerId = "auto",
    int MessageTokenOverhead = 16,
    int ImageTokenBudget = 4096);