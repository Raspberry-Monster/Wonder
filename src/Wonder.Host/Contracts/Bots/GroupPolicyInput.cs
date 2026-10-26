namespace Wonder.Host.Contracts;

public sealed record GroupPolicyInput(
    bool RespondToMention = true,
    bool RespondToReply = true,
    string CommandPrefix = "/wonder",
    bool PersistMessages = true);