using System.Reflection;
using System.Text.Json.Serialization;

namespace Wonder.Core;

// Stable wire names are also the existing database values. Do not rename them with CLR members.
[AttributeUsage(AttributeTargets.Enum)]
public sealed class StringStoredEnumAttribute : Attribute;

[StringStoredEnum]
public enum NoteAuthorKind { Agent = 0, User = 1 }
[StringStoredEnum]
public enum NoteClaimKind
{
    [JsonStringEnumMemberName("observation")] Observation = 0,
    [JsonStringEnumMemberName("guess")] Guess = 1,
    [JsonStringEnumMemberName("uncertain")] Uncertain = 2
}
[StringStoredEnum]
public enum NoteDependencyTargetKind { Chat = 0, Message = 1, Summary = 2, Note = 3 }
[StringStoredEnum]
public enum ReflectionPriority { Normal = 0, Important = 1 }
[StringStoredEnum]
public enum ReflectionKind
{
    [JsonStringEnumMemberName("light")] Light = 0,
    [JsonStringEnumMemberName("full")] Full = 1
}
[StringStoredEnum]
public enum ReflectionGenerationStatus { Queued = 0, Running = 1, Completed = 2, Cancelled = 3 }
[StringStoredEnum]
public enum ReflectionCancellationReason
{
    AuthorizationChanged = 0, SourceChanged = 1, NoEligibleSources = 2, PrivacyRevocation = 3
}
public enum ReflectionRunDisplayStatus { Queued = 0, Running = 1, ReflectionCompleted = 2, Failed = 3, Cancelled = 4 }
public enum ReflectionAdmissionOutcome
{
    Accepted = 0, NoNewEligibleMessages = 1, AlreadyScheduled = 2, AlreadyProcessed = 3,
    ResultNotReusable = 4, FailedRetryExisting = 5
}
[StringStoredEnum]
public enum ForceCompensationOutcome { Refunded = 0, AlreadyProcessed = 1, Expired = 2, SupersededByReset = 3 }
[StringStoredEnum]
public enum AgentAction
{
    [JsonStringEnumMemberName("interaction_mute")] Mute = 0,
    [JsonStringEnumMemberName("interaction_reset_force")] ResetForce = 1,
    [JsonStringEnumMemberName("interaction_unmute")] Unmute = 2,
    [JsonStringEnumMemberName("interaction_no_reply")] NoReply = 3,
    [JsonStringEnumMemberName("notes_create")] NoteCreate = 4,
    [JsonStringEnumMemberName("notes_search")] NoteSearch = 5,
    [JsonStringEnumMemberName("notes_read")] NoteRead = 6,
    [JsonStringEnumMemberName("notes_update")] NoteUpdate = 7,
    [JsonStringEnumMemberName("notes_delete")] NoteDelete = 8,
    [JsonStringEnumMemberName("reflection_request_review")] ReflectionRequest = 9,
    [JsonStringEnumMemberName("reflection_list_runs")] ReflectionList = 10,
    [JsonStringEnumMemberName("reflection_get_run")] ReflectionGet = 11
}
public enum AgentCapability { Interaction = 0, Notes = 1, Reflection = 2 }
[StringStoredEnum]
public enum DeletionResourceKind
{
    Memory = 0, Note = 1,
    // Retires context from a removed source without deleting unrelated private facts.
    MemoryContext = 2
}
[StringStoredEnum]
public enum MessageRole
{
    [JsonStringEnumMemberName("user")] User = 0,
    [JsonStringEnumMemberName("assistant")] Assistant = 1
}
[StringStoredEnum]
public enum JobKind
{
    [JsonStringEnumMemberName("chat")] Chat = 0,
    [JsonStringEnumMemberName("outgoing")] Outgoing = 1,
    [JsonStringEnumMemberName("reflection")] Reflection = 2,
    [JsonStringEnumMemberName("emotion")] Emotion = 3,
    [JsonStringEnumMemberName("relationship")] Relationship = 4,
    [JsonStringEnumMemberName("memory")] Memory = 5,
    [JsonStringEnumMemberName("memory-maintenance")] MemoryMaintenance = 6,
    [JsonStringEnumMemberName("embedding")] Embedding = 7,
    [JsonStringEnumMemberName("maintenance")] Maintenance = 8,
    [JsonStringEnumMemberName("narrative")] Narrative = 9, // Retired; retained for historical queue records.
    [JsonStringEnumMemberName("summary")] Summary = 10,
    [JsonStringEnumMemberName("deletion-ledger")] Ledger = 11, // Retired; never reuse this value.
    [JsonStringEnumMemberName("memory-privacy")] MemoryPrivacy = 12
}
public enum DeliveryResolutionAction
{
    [JsonStringEnumMemberName("confirm-sent")] ConfirmSent = 0,
    [JsonStringEnumMemberName("retry")] Retry = 1
}
public enum NoteEffectiveStatus { Current = 0, Superseded = 1 }
public enum NoteSourceType { AgentNote = 0, UserNote = 1 }
public enum AgentReceiptStatus { Accepted = 0 }

public static class EnumWire
{
    private static class Map<T> where T : struct, Enum
    {
        internal static readonly IReadOnlyDictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(x => x,
            x => typeof(T).GetField(x.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? x.ToString());
        internal static readonly IReadOnlyDictionary<string, T> Values = Names.ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);
    }

    public static string Name<T>(T value) where T : struct, Enum => Map<T>.Names.TryGetValue(value, out var name)
        ? name : throw new ArgumentOutOfRangeException(nameof(value), value, "Undefined enum value.");
    public static bool TryParse<T>(string name, out T value) where T : struct, Enum => Map<T>.Values.TryGetValue(name, out value);
    public static T Parse<T>(string name) where T : struct, Enum => TryParse<T>(name, out var value)
        ? value : throw new ArgumentException($"Unknown {typeof(T).Name} value.", nameof(name));
}

public enum AgentToolError
{
    StaleExecution = 0,
    AuthorizationChanged = 1,
    MemoryAuthorizationRequired = 2,
    InvalidArguments = 3,
    OperationConflict = 4,
    AmbiguousRetry = 5,
    TurnTerminated = 6,
    StaleSource = 7,
    NotesAuthorizationRequired = 8,
    NoteNotFound = 9,
    VersionConflict = 10,
    QuotaTimeZoneChanged = 11,
    QuotaExhausted = 12,
    InvalidExpiry = 13,
    ReasonTooLong = 14,
    AlreadyMuted = 15,
    ForceTurnRequired = 16,
    StaleMuteSession = 17,
    UnavailableTool = 18
}
