namespace Wonder.Core;

public sealed class MuteSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Reason { get; set; }
    public int ForceRemaining { get; set; } = 3;
    public long ResetGeneration { get; set; }
    public long Revision { get; set; }
}

public sealed class ForceReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public Guid MuteSessionId { get; set; }
    public Guid UserId { get; set; }
    public string PlatformEventKey { get; set; } = "";
    public Guid MessageId { get; set; }
    public Guid ChatJobId { get; set; }
    public long ChargeGeneration { get; set; }
    public int RemainingAfter { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? ProcessingStartedAt { get; set; }
    public ForceCompensationOutcome? CompensationStatus { get; set; }
}

// Ordinal is the durable slot within a Chat, independent of vendor tool-call IDs.
// A regenerated call that changes the action or arguments at a committed slot conflicts.
public sealed class AgentOperation
{
    public Guid ExecutorLeaseId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatJobId { get; set; }
    public int Ordinal { get; set; }
    public Guid BotId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public AgentAction Action { get; set; }
    public string ArgumentsHash { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
    public DateTimeOffset CommittedAt { get; set; }
}

public sealed class ChatDisposition
{
    public Guid ChatJobId { get; set; }
    public Guid ForceReceiptId { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
}
