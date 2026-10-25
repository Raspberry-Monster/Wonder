using System.Text.Json;
using System.Text.Json.Nodes;
using Wonder.Core;

namespace Wonder.Infrastructure;

// Used only by the explicit offline converter, never by request or worker paths.
public static class LegacyDeletionLedgerConverter
{
    private enum LegacyScope { Private = 0, GroupUser = 1, GroupShared = 2, GlobalPublic = 3, UserShared = 4, SpaceShared = 5 }

    public static string Convert(string json)
    {
        var rows = JsonNode.Parse(json) as JsonArray ?? throw new JsonException("Expected a deletion ledger array.");
        foreach (var node in rows)
        {
            var row = node as JsonObject ?? throw new JsonException("Invalid deletion ledger row.");
            if (!row.ContainsKey("recordVersion") && !row.ContainsKey("scope") && row.ContainsKey("resourceKind")) continue;
            var version = row["recordVersion"]?.GetValue<int>() ?? 1;
            var kind = row["resourceKind"]?.GetValue<string>() ?? "Memory";
            if (version == 2 && kind == "Note") row["resourceKind"] = "Note";
            else if (version == 1 && kind == "Memory")
            {
                var rawScope = row["scope"];
                var scope = rawScope is null ? LegacyScope.Private : rawScope.GetValueKind() switch
                {
                    JsonValueKind.String => EnumWire.Parse<LegacyScope>(rawScope.GetValue<string>()),
                    JsonValueKind.Number when Enum.IsDefined((LegacyScope)rawScope.GetValue<int>()) => (LegacyScope)rawScope.GetValue<int>(),
                    _ => throw new JsonException("Unknown legacy memory scope.")
                };
                row["resourceKind"] = scope == LegacyScope.Private ? "Memory" : "MemoryContext";
                if (scope != LegacyScope.Private)
                {
                    row["factKeyHash"] = "";
                    row["resourceId"] = null;
                    row["resourceVersion"] = null;
                }
            }
            else throw new JsonException("Unsupported legacy deletion ledger version or resource.");
            row.Remove("recordVersion"); row.Remove("scope"); row.Remove("scopeOwnerId");
        }
        return Json.Write(DeletionLedger.Read(rows.ToJsonString()));
    }
}
