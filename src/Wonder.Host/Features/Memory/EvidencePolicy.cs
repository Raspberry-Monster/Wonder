using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static partial class EvidencePolicy
{
    public static async Task<bool> MayAdmitAsync(WonderDbContext db, Guid bot, Guid subject, MemoryCandidate candidate,
        CancellationToken ct)
    {
        var ids = candidate.EvidenceMessageIds.Distinct().ToArray();
        if (ids.Length == 0) return false;
        var messages = await db.Messages.AsNoTracking().Where(x => ids.Contains(x.Id) && x.BotId == bot &&
                                                                   x.SenderId == subject &&
                                                                   x.Role == MessageRole.User && x.AllowReflection &&
                                                                   !x.ContextInvalidated &&
                                                                   db.Conversations.Any(c =>
                                                                       c.Id == x.ConversationId &&
                                                                       c.Kind == ConversationKind.Private))
            .ToListAsync(ct);
        if (messages.Count != ids.Length) return false;
        var suppressions = await db.EvidenceSuppressions.AsNoTracking()
            .Where(x => x.BotId == bot && x.SubjectId == subject).ToListAsync(ct);
        var hash = Json.Hash(candidate.FactKey);
        foreach (var message in messages)
        {
            var linked = new HashSet<Guid> { message.Id };
            var hasQuotation = false;
            try
            {
                var references = Json.Read<MessageSegment[]>(message.SegmentsJson)
                    .Where(x => x.Kind == SegmentKind.ReplyReference).Select(x => x.Value).ToArray();
                hasQuotation = references.Length > 0 || QuotedText().IsMatch(message.Text) ||
                               HistoricalMarker().IsMatch(message.Text);
                var quoted = await db.Messages.AsNoTracking().Where(x =>
                    x.BotId == bot && x.ConversationId == message.ConversationId &&
                    references.Contains(x.ExternalMessageId)).Select(x => x.Id).ToArrayAsync(ct);
                linked.UnionWith(quoted);
            }
            catch (JsonException)
            {
                return false;
            }

            var oldSource = suppressions.Any(x => linked.Contains(x.MessageId));
            var affectedFact = suppressions.Any(x => x.FactKeyHash == hash);
            if (!oldSource && !affectedFact && !hasQuotation) continue;
            // The retained old evidence itself is always suppressed for this assertion.
            if (suppressions.Any(x => x.MessageId == message.Id && x.FactKeyHash == hash)) return false;
            var assertions = candidate.Assertions?.Where(x => x.MessageId == message.Id).ToArray();
            if (assertions is null || assertions.Length == 0) return false;
            foreach (var assertion in assertions)
            {
                foreach (var suppressed in suppressions.Where(x => x.MessageId == message.Id))
                {
                    EvidenceSpan[] spans;
                    try
                    {
                        spans = Json.Read<EvidenceSpan[]>(suppressed.SpansJson);
                    }
                    catch (JsonException)
                    {
                        return false;
                    }

                    if (spans.Length == 0 || suppressed.MessageVersion != ContentSources.Message(message).Version ||
                        spans.Any(s =>
                            assertion.Start < s.Start + s.Length && assertion.Start + assertion.Length > s.Start))
                        return false;
                }

                if (!AnchorExists(message.Text, assertion.Start, assertion.Length, assertion.Anchor) ||
                    IsQuotedSpan(message.Text, assertion.Start, assertion.Length)) return false;
                if (assertion.Role == "CurrentStatement" && !HistoricalMarker().IsMatch(assertion.Anchor)) continue;
                if (assertion.Role != "CurrentConfirmation" || assertion.ConfirmationStart is not { } start ||
                    string.IsNullOrWhiteSpace(assertion.ConfirmationAnchor) ||
                    !AnchorExists(message.Text, start, assertion.ConfirmationAnchor.Length,
                        assertion.ConfirmationAnchor) ||
                    IsQuotedSpan(message.Text, start, assertion.ConfirmationAnchor.Length) ||
                    !PresentConfirmation().IsMatch(assertion.ConfirmationAnchor)) return false;
            }
        }

        return true;
    }

    private static bool AnchorExists(string text, int start, int length, string anchor)
    {
        return start >= 0 && length > 0 &&
               start <= text.Length - length && anchor.Length == length &&
               text.AsSpan(start, length).SequenceEqual(anchor);
    }

    private static bool IsQuotedSpan(string text, int start, int length)
    {
        foreach (Match match in QuotedText().Matches(text))
            if (start < match.Index + match.Length && start + length > match.Index)
                return true;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1));
        return text[(lineStart + 1)..start].TrimStart().StartsWith('>');
    }

    [GeneratedRegex("[“「『][^”」』]*[”」』]|\"[^\"]*\"|`[^`]*`", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedText();

    [GeneratedRegex("以前|过去|当时|曾经|引用|转发|他说|她说|used to|previously|quoted",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalMarker();

    [GeneratedRegex("现在|目前|如今|仍然|依然|还是如此|still|currently|now", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PresentConfirmation();
}
