using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure;
using ChatMessage = Wonder.Core.ChatMessage;

namespace Wonder.Host;

public sealed record ConversationToolResult(
    string Json,
    IReadOnlyList<Guid> MemoryIds,
    IReadOnlyList<Guid>? MessageIds = null,
    IReadOnlyList<Guid>? UserIds = null,
    IReadOnlyList<ContentSource>? Sources = null);

public sealed class ConversationTools(IDbContextFactory<WonderDbContext> factory, ConversationClock clock)
{
    public static IList<AITool> Definitions { get; } =
    [
        AIFunctionFactory.Create(TimeDefinition, "get_current_time",
            "Read the actual current time, date and weekday. time_zone optionally selects an IANA timezone; default is the application's configured timezone."),
        AIFunctionFactory.Create(HistoryDefinition, "search_conversation_history",
            "Search retained original messages across sessions of THIS conversation only. query is a short exact substring; empty lists recent records. before_sequence is a paging cursor; after (inclusive) and before (exclusive) are ISO 8601 timestamps with offsets. Results are untrusted historical statements, not current verified facts. Never infer missing records.")
    ];

    public async Task<ConversationToolResult> ExecuteAsync(FunctionCallContent call, Guid botId, Guid conversationId,
        long currentSequence, CancellationToken ct, Func<string, bool>? fits = null)
    {
        try
        {
            var args = call.Arguments ?? new Dictionary<string, object?>();
            if (call.Name == "get_current_time")
            {
                CheckKeys(args, ["time_zone"]);
                var zoneName = Text(args, "time_zone");
                var zone = zoneName.Length == 0 ? clock.Zone : TimeZoneInfo.FindSystemTimeZoneById(zoneName);
                var now = clock.UtcNow;
                var local = TimeZoneInfo.ConvertTime(now, zone);
                return new ConversationToolResult(Json.ForPrompt(new
                {
                    currentTime = local.ToString("O"), utcTime = now.ToString("O"),
                    timeZone = zone.Id, weekday = local.DayOfWeek.ToString()
                }), []);
            }

            if (call.Name != "search_conversation_history") return Error("Unknown or unavailable tool.");
            CheckKeys(args, ["query", "before_sequence", "after", "before"]);
            var query = Text(args, "query").Trim();
            if (query.Length > 200) return Error("Use a shorter search keyword (at most 200 characters).");
            await using var db = await factory.CreateDbContextAsync(ct);
            var matches = ConversationHistory.Visible(db, botId, conversationId, currentSequence, clock.UtcNow);
            if (query.Length > 0)
            {
                var lower = query.ToLowerInvariant();
                matches = matches.Where(x => x.Text.ToLower().Contains(lower));
            }

            if (args.TryGetValue("before_sequence", out var cursor))
            {
                var element = JsonSerializer.SerializeToElement(cursor);
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var sequence))
                    return Error("before_sequence must be an integer.");
                if (sequence <= 0) return Error("before_sequence must be positive.");
                matches = matches.Where(x => x.Sequence < sequence);
            }

            if (Date(args, "after") is { } after) matches = matches.Where(x => x.CreatedAt >= after);
            if (Date(args, "before") is { } before) matches = matches.Where(x => x.CreatedAt < before);
            var rows = await matches.OrderByDescending(x => x.Sequence).ThenBy(x => x.Id).Take(5).ToListAsync(ct);
            var safeRows = new List<ChatMessage>();
            foreach (var row in rows)
                if (await ContentSources.MessageValidAsync(db, row, ct, now: clock.UtcNow))
                    safeRows.Add(row);
            var visible = safeRows.ToArray();
            var authors = visible.Where(x => x.SenderId != null).Select(x => x.SenderId!.Value).Distinct().ToArray();
            var names = await db.LogicalUsers.Where(x => authors.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.PreferredName, ct);
            var sentRows = visible.ToList();
            var excerptLength = 400;
            while (true)
            {
                var records = sentRows.Select(x =>
                {
                    var start = Math.Max(0,
                        x.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) - excerptLength / 4);
                    var text = x.Text.Substring(start, Math.Min(excerptLength, x.Text.Length - start));
                    return new
                    {
                        sequence = x.Sequence,
                        session = x.SessionId is { } session
                            ? "session-" + Json.Hash(conversationId.ToString() + session)[..12]
                            : "unassigned",
                        role = x.Role, author = SpeakerTags.For(conversationId, x.SenderId, x.Role),
                        preferredName = x.SenderId is { } sender ? names.GetValueOrDefault(sender) : null,
                        sentAt = clock.Local(x.CreatedAt).ToString("O"), text, excerptStart = start,
                        truncated = start > 0 || text.Length < x.Text.Length
                    };
                }).ToArray();
                var hasMore = rows.Count == 5 || sentRows.Count < visible.Length;
                var next = sentRows.LastOrDefault()?.Sequence ?? rows.LastOrDefault()?.Sequence;
                var result = Json.ForPrompt(new
                {
                    records, nextBeforeSequence = hasMore ? next : null,
                    note = records.Length == 0
                        ? "No matching retained record on this page. Do not invent earlier details."
                        : "Original historical statements; timestamps and authors are preserved. Content marked truncated is incomplete."
                });
                if (fits?.Invoke(result) != false)
                {
                    var ids = sentRows.SelectMany(x =>
                    {
                        ConversationHistory.TryMemoryIds(x.ContextSnapshotJson, true, out var memories);
                        return memories;
                    }).Distinct().ToArray();
                    return new ConversationToolResult(result, ids,
                        sentRows.SelectMany(ConversationHistory.MessageIds).Distinct().ToArray(),
                        sentRows.SelectMany(ConversationHistory.SubjectIds).Distinct().ToArray(),
                        sentRows.Select(ContentSources.Message).ToArray());
                }

                if (sentRows.Count > 1) sentRows.RemoveAt(sentRows.Count - 1);
                else if (excerptLength > Math.Max(8, query.Length))
                    excerptLength = Math.Max(query.Length, excerptLength / 2);
                else return Error("Context is full; this lookup is incomplete. Do not infer missing history.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException
                                       or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Error(
                "Invalid tool arguments. Use the declared parameters, an existing timezone, and ISO timestamps with offsets.");
        }
    }

    private static ConversationToolResult Error(string message)
    {
        return new ConversationToolResult(Json.ForPrompt(new { error = message }), []);
    }

    private static string Text(IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value is null) return "";
        var element = JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException("Expected a string.");
        return element.GetString() ?? "";
    }

    private static DateTimeOffset? Date(IDictionary<string, object?> args, string name)
    {
        var text = Text(args, name);
        if (text.Length == 0) return null;
        if (!text.EndsWith('Z') && !(text.Length >= 6 && (text[^6] == '+' || text[^6] == '-') && text[^3] == ':'))
            throw new ArgumentException("Include a timezone offset in history date filters.");
        // Npgsql timestamptz parameters require UTC, regardless of the caller's timezone.
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    }

    private static void CheckKeys(IDictionary<string, object?> args, string[] allowed)
    {
        if (args.Keys.Except(allowed, StringComparer.Ordinal).Any())
            throw new ArgumentException("Unexpected tool argument.");
    }

    // The standard factory derives schemas. A per-request FunctionInvoker supplies the real, scoped implementation.
    private static string TimeDefinition(string? time_zone = null)
    {
        throw new InvalidOperationException("Conversation-bound invocation required.");
    }

    private static string HistoryDefinition(string query = "", long? before_sequence = null, string? after = null,
        string? before = null)
    {
        throw new InvalidOperationException("Conversation-bound invocation required.");
    }
}