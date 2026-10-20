namespace Wonder.Core;

public static class SpeakerTags
{
    // Local IDs stay in the application; only a conversation-scoped anonymous label enters prompts.
    public static string For(Guid conversationId, Guid? senderId, string role = "user")
    {
        if (role == "assistant") return "assistant";
        return senderId is { } id
            ? "speaker-" + Json.Hash($"{conversationId:N}:{id:N}")[..16].ToLowerInvariant()
            : "unknown";
    }
}
