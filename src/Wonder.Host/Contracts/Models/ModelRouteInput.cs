namespace Wonder.Host.Contracts;

// Active configuration purposes retain the persisted TaskKind numbers; 3 is historical usage only.
public enum ModelTaskKind
{
    Chat = 0,
    LightReflection = 1,
    StrongReflection = 2,
    RollingSummary = 4,
    Embedding = 5
}

public sealed record ModelRouteInput(
    ModelTaskKind TaskKind,
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