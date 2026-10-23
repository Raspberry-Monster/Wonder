using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wonder.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };

    // Model message text is data, never inline HTML. Keep storage serialization stable for payload hashes.
    private static readonly JsonSerializerOptions PromptOptions = new(Options)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ForPrompt<T>(T value)
    {
        return JsonSerializer.Serialize(value, PromptOptions);
    }

    public static string Write<T>(T value)
    {
        return JsonSerializer.Serialize(value, Options);
    }

    public static T Read<T>(string value)
    {
        return JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException("Empty payload.");
    }

    public static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    }
}

public static class JobTypes
{
    public const string Ledger = "deletion-ledger";

    public const string Chat = "chat",
        Outgoing = "outgoing",
        Reflection = "reflection",
        Emotion = "emotion",
        Relationship = "relationship",
        Memory = "memory",
        MemoryMaintenance = "memory-maintenance",
        Embedding = "embedding",
        Maintenance = "maintenance",
        Narrative = "narrative",
        Summary = "summary";
}

public sealed record IdPayload(Guid Id);

public sealed record ReflectionJob(Guid SessionId, Guid SubjectId, long Boundary, long Epoch, string Kind);

public sealed record SummaryJob(Guid SessionId, long ThroughSequence);

public sealed record DailyJob(DateOnly Date);