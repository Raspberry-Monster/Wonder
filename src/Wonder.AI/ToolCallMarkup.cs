using System.Text.RegularExpressions;

namespace Wonder.AI;

// This is an output guard, not a tool parser. Only structured FunctionCallContent may invoke tools.
public static class ToolCallMarkup
{
    private static readonly Regex Marker = new(@"</?[|｜]+DSML", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    public static bool Contains(string text) => Marker.IsMatch(text);
}
