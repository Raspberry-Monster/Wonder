using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wonder.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

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

public sealed record IdPayload(Guid Id);

public sealed record ReflectionJob(Guid SessionId, Guid SubjectId, long Boundary, long Epoch, ReflectionKind Kind);

public sealed record SummaryJob(Guid SessionId, long ThroughSequence, SummaryProjectionRequest? Projection = null);
public sealed record SummaryProjectionRequest(Guid SummaryId, Guid SubjectId, long BodyRevision,
    long ProjectionRevision, Guid[] MemoryIds, Guid[] MessageIds, DateTimeOffset ExpiresAt);

public sealed record DailyJob(DateOnly Date);
