using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class RelationshipClaims
{
    public static async Task InvalidateAsync(WonderDbContext db, Guid bot, IReadOnlyCollection<Guid> memoryIds,
        IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        var claims = await db.RelationshipClaims.Where(x => x.BotId == bot).ToListAsync(ct);
        foreach (var claim in claims)
        {
            var memoryKnown = ConversationHistory.TryMemoryIds(claim.MemorySourcesJson, false, out var memories);
            var messageKnown = ConversationHistory.TryMemoryIds(claim.EvidenceJson, false, out var messages);
            if (!(memoryKnown && memories.Any(memoryIds.Contains)) && !(messageKnown && messages.Any(messageIds.Contains))) continue;
            // Explicit and Unknown boundaries remain independently binding; Memory is never an authorization revocation.
            if (claim.Kind == "Boundary" && claim.Origin != "Inferred") continue;
            var surviving = messages.Except(messageIds).ToArray();
            var proof = await db.Messages.AsNoTracking().Where(x => surviving.Contains(x.Id) && x.BotId == bot &&
                x.SenderId == claim.SubjectId && x.Role == MessageRole.User && !x.ContextInvalidated && x.AllowReflection).ToListAsync(ct);
            // Distinct roots are necessary but not a numerical majority vote. Preserve Current only for direct,
            // unchanged explicit statements; inferred multi-source sufficiency requires a later independent review.
            claim.Qualification = claim.Origin == "Explicit" && proof.Any(x => x.Text.Contains(claim.Text, StringComparison.Ordinal))
                ? "Current" : messageIds.Count > 0 ? "Blocked" : "Tentative";
            claim.Revision++;
        }
    }

    public static async Task<string> ForModelAsync(WonderDbContext db, RelationshipState state, CancellationToken ct) =>
        (await ProjectionAsync(db, state, ct)).Text;

    public static async Task<(string Text, ContentSource Source)> ProjectionAsync(WonderDbContext db, RelationshipState state, CancellationToken ct)
    {
        var claims = await db.RelationshipClaims.AsNoTracking().Where(x => x.RelationshipId == state.Id).OrderBy(x => x.Id).ToListAsync(ct);
        var legacy = Json.Read<string[]>(state.PreferencesJson);
        var rows = new List<object>();
        foreach (var boundary in Json.Read<string[]>(state.BoundariesJson).Where(x => !claims.Any(c => c.Kind == "Boundary" && c.Text == x)))
            rows.Add(new { text = boundary, qualification = "IndependentBoundary", origin = "Unknown", kind = "Boundary" });
        var hasDeletionRisk = await db.EvidenceSuppressions.AnyAsync(x => x.BotId == state.BotId && x.SubjectId == state.UserId, ct);
        foreach (var text in legacy.Where(x => !claims.Any(c => c.Kind == "Preference" && c.Text == x)))
            if (!hasDeletionRisk) rows.Add(new { text, qualification = "Unverified", origin = "Unknown", kind = "Preference" });
        foreach (var claim in claims)
        {
            if (claim.Qualification == "Blocked") continue;
            if (claim.Kind == "Boundary" && claim.Origin is "Explicit" or "Unknown")
            { rows.Add(new { text = claim.Text, qualification = "IndependentBoundary", origin = claim.Origin, kind = claim.Kind }); continue; }
            if (!ConversationHistory.TryMemoryIds(claim.EvidenceJson, false, out var ids) ||
                !ConversationHistory.TryMemoryIds(claim.MemorySourcesJson, false, out var memoryIds)) continue;
            if (memoryIds.Count > 0 && await db.Memories.CountAsync(x => memoryIds.Contains(x.Id) && x.BotId == state.BotId &&
                x.SubjectId == state.UserId && x.ArchivedAt == null && !x.IsOutdated && x.Status != MemoryStatus.Revoked, ct) != memoryIds.Distinct().Count()) continue;
            if (ids.Count > 0 && await db.Messages.CountAsync(x => ids.Contains(x.Id) && x.BotId == state.BotId && !x.ContextInvalidated, ct) != ids.Distinct().Count()) continue;
            if (await db.EvidenceSuppressions.AnyAsync(x => x.BotId == state.BotId && ids.Contains(x.MessageId), ct)) continue;
            if (hasDeletionRisk && claim.Origin == "Unknown") continue;
            rows.Add(new { text = claim.Text, qualification = claim.Qualification, origin = claim.Origin, kind = claim.Kind });
        }
        // These are contextual references, never permission changes or /mute controls.
        var projectionText = Json.ForPrompt(new { references = rows, caution = "Tentative and Unverified claims are non-binding references, not current user facts. Preserve independently explicit boundaries. Permissions and mute are controlled by the server." });
        var version = Json.Hash(Json.Write(new { state.Revision, claims = claims.Select(x => new { x.Id, x.Revision }), text = projectionText }));
        return (projectionText, new ContentSource("Relationship", state.Id, version));
    }
}
