namespace Wonder.Host.Contracts;

internal static class RequestRules
{
    public static bool IsUrl(string? value, params string[] schemes)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && schemes.Contains(uri.Scheme) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }
}