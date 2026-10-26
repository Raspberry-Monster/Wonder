using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

// A separate extraction view, never the retained original body. Unknown scope is not projectable.
public static class SafeReflectionEvidence
{
    public static async Task<(List<ChatMessage> Rows, Dictionary<Guid, string> OriginalHashes)> ReadAsync(
        WonderDbContext db, Guid bot, Guid subject, Guid session, long sequence, long boundary, int offset, bool light,
        CancellationToken ct)
    {
        var rows = await db.Messages.AsNoTracking().Where(x =>
                x.BotId == bot && x.SessionId == session && x.SenderId == subject &&
                x.AllowReflection && !x.ContextInvalidated && x.Sequence <= boundary &&
                (x.Sequence > sequence || (offset > 0 && x.Sequence == sequence)) &&
                (!light || x.Sequence == boundary) &&
                db.EvidenceSuppressions.Any(s => s.BotId == bot && s.MessageId == x.Id))
            .OrderBy(x => x.Sequence).Take(100).ToListAsync(ct);
        var result = new List<ChatMessage>();
        var hashes = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            var text = await ProjectAsync(db, row, ct);
            if (text is null) continue;
            hashes[row.Id] = TextBatch.Fingerprint(row.Text);
            // Fixed-width masking preserves exact offsets, but no suppressed text or source snapshot enters the model.
            result.Add(new ChatMessage
            {
                Id = row.Id, Sequence = row.Sequence, SenderId = row.SenderId, Role = row.Role,
                CreatedAt = row.CreatedAt, Text = text
            });
        }

        return (result, hashes);
    }

    public static async Task<string?> ProjectAsync(WonderDbContext db, ChatMessage row, CancellationToken ct)
    {
        var suppressions = await db.EvidenceSuppressions.AsNoTracking()
            .Where(x => x.BotId == row.BotId && x.MessageId == row.Id).ToListAsync(ct);
        var spans = new List<EvidenceSpan>();
        foreach (var suppression in suppressions)
            try
            {
                var blocked = Json.Read<EvidenceSpan[]>(suppression.SpansJson);
                if (suppression.MessageVersion != ContentSources.Message(row).Version || blocked.Length == 0 ||
                    blocked.Any(s => s.Start < 0 || s.Length < 1 || s.Start > row.Text.Length - s.Length)) return null;
                spans.AddRange(blocked);
            }
            catch (JsonException)
            {
                return null;
            }

        var text = row.Text.ToCharArray();
        foreach (var span in spans) Array.Fill(text, '＊', span.Start, span.Length);
        return new string(text);
    }
}