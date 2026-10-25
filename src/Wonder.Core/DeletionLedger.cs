using System.Text.Json;

namespace Wonder.Core;

public static class DeletionLedger
{
    public static MemoryRevocation[] Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected a deletion ledger array.");
        foreach (var row in document.RootElement.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("resourceKind", out _) ||
                row.TryGetProperty("scope", out _) || row.TryGetProperty("scopeOwnerId", out _) || row.TryGetProperty("recordVersion", out _))
                throw new JsonException("Convert the legacy deletion ledger with --convert-ledger before replay.");
        var rows = Json.Read<MemoryRevocation[]>(json);
        foreach (var row in rows)
        {
            if (row.Id == Guid.Empty || row.BotId == Guid.Empty || row.UserId == Guid.Empty || row.Epoch < 0 ||
                !Enum.IsDefined(row.ResourceKind) ||
                (row.ResourceKind == DeletionResourceKind.Note && (row.ResourceId is null || row.ResourceId == Guid.Empty)) ||
                (row.ResourceKind == DeletionResourceKind.Memory && (row.FactKeyHash.Length != 64 || !row.FactKeyHash.All(Uri.IsHexDigit))) ||
                (row.ResourceKind == DeletionResourceKind.MemoryContext && (row.ResourceId != null || row.FactKeyHash != "")))
                throw new JsonException("Invalid deletion ledger target.");
        }
        return rows;
    }
}
