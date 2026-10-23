namespace Wonder.Host.Contracts;

public sealed record BindingInput(
    string PlatformAccountId,
    string WebSocketUrl,
    string? AccessToken,
    bool Enabled = true);