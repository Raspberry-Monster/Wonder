using System.Text.Json;
using System.Text.Json.Nodes;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.Host;

public static class MemoryAssertionPayload
{
    public static bool Matches(MemoryCandidate candidate, IReadOnlySet<string> hashes)
    {
        if (hashes.Contains(Json.Hash(Json.Write(candidate)))) return true;
        // Compatibility with the actual pre-assertions record shape; no semantic or same-session guessing.
        return candidate.Assertions is null && hashes.Contains(Json.Hash(Json.Write(new
        {
            candidate.FactKey, candidate.Text, candidate.Explicit, candidate.Sensitive, candidate.EvidenceMessageIds
        })));
    }

    public static bool Matches(JsonNode assertion, IReadOnlySet<string> hashes)
    {
        return hashes.Contains(Json.Hash(assertion.ToJsonString(Json.Options))) ||
               Matches(Json.Read<MemoryCandidate>(assertion.ToJsonString(Json.Options)), hashes);
    }

    public static async Task<bool> RemainingSafeAsync(WonderDbContext db, Guid bot, Guid subject, JsonArray assertions,
        CancellationToken ct)
    {
        foreach (var assertion in assertions)
        {
            if (assertion is null) return false;
            try
            {
                var candidate = Json.Read<MemoryCandidate>(assertion.ToJsonString(Json.Options));
                if (candidate.EvidenceMessageIds is null || candidate.FactKey is null || candidate.Text is null ||
                    !await EvidencePolicy.MayAdmitAsync(db, bot, subject, candidate, ct)) return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return true;
    }
}