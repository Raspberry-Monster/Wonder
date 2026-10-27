namespace Wonder.Core.Models;

public sealed record PlatformLoginStatus(PlatformLoginState State, string? Error = null);