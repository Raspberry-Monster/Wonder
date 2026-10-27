using System.Text.RegularExpressions;

namespace Wonder.Host;

internal static partial class BindingRegex
{
    [GeneratedRegex("^[1-9][0-9]*$")]
    internal static partial Regex PlatformAccountId();
}