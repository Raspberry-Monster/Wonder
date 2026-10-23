namespace Wonder.Host.Contracts;

public sealed record GroupPolicyInput(
    bool RespondToMention = true,
    bool RespondToReply = true,
    string CommandPrefix = "/wonder",
    bool PersistMessages = true,
    bool AllowReflection = true,
    bool ProactiveEnabled = false,
    Guid? ContextSpaceId = null);