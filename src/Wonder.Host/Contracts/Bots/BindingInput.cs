namespace Wonder.Host.Contracts;

public sealed record BindingInput(
    string PlatformAccountId,
    string ApiBaseUrl,
    string? AccessToken = null,
    bool Enabled = true,
    bool ClearAccessToken = false);
