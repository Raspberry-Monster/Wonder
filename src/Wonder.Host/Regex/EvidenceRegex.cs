using System.Text.RegularExpressions;

namespace Wonder.Host;

internal static partial class EvidenceRegex
{
    [GeneratedRegex("[“「『][^”」』]*[”」』]|\"[^\"]*\"|`[^`]*`", RegexOptions.CultureInvariant)]
    internal static partial Regex QuotedText();

    [GeneratedRegex("以前|过去|当时|曾经|引用|转发|他说|她说|used to|previously|quoted",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex HistoricalMarker();

    [GeneratedRegex("现在|目前|如今|仍然|依然|还是如此|still|currently|now", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PresentConfirmation();
}