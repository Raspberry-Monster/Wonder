using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Wonder.Core;

public static class EventContracts
{
    public const int Version = 2;

    public static IReadOnlyDictionary<string, Type> PayloadTypes { get; } = new Dictionary<string, Type>
    {
        [JobTypes.Chat] = typeof(IdPayload), [JobTypes.Outgoing] = typeof(IdPayload),
        [JobTypes.Reflection] = typeof(ReflectionJob), [JobTypes.Emotion] = typeof(IdPayload),
        [JobTypes.Relationship] = typeof(IdPayload), [JobTypes.Memory] = typeof(IdPayload),
        [JobTypes.MemoryMaintenance] = typeof(IdPayload), [JobTypes.Embedding] = typeof(IdPayload),
        [JobTypes.Maintenance] = typeof(DailyJob), [JobTypes.Narrative] = typeof(DailyJob),
        [JobTypes.Summary] = typeof(SummaryJob), [JobTypes.Ledger] = typeof(IdPayload)
    };

    public static void ValidateVersion(OutboxMessage job)
    {
        if (job.SchemaVersion != Version || !PayloadTypes.ContainsKey(job.Type))
            throw new JsonException("Unsupported event type or version; run the database migration.");
    }

    public static JsonObject Schema()
    {
        var options = new JsonSerializerOptions(Json.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var branches = new JsonArray();
        foreach (var (name, payload) in PayloadTypes)
        {
            var schema = options.GetJsonSchemaAsNode(typeof(OutboxMessage));
            var properties = schema["properties"]!;
            properties["type"] = new JsonObject { ["const"] = name };
            properties["schemaVersion"] = new JsonObject { ["const"] = Version };
            properties["payload"] = new JsonObject
            {
                ["type"] = "string", ["contentMediaType"] = "application/json",
                ["contentSchema"] = options.GetJsonSchemaAsNode(payload)
            };
            schema["required"] = new JsonArray("id", "botId", "type", "schemaVersion", "payload", "correlationId",
                "createdAt");
            branches.Add(schema);
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["title"] = "Wonder durable events v2",
            ["oneOf"] = branches
        };
    }
}