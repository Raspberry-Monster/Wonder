namespace Wonder.Core;

public sealed class ReflectionGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public Guid SessionId { get; set; }
    public Guid SubjectId { get; set; }
    public long Epoch { get; set; }
    public long RawBoundary { get; set; }
    public string SourceSignature { get; set; } = "";
    public Guid JobId { get; set; }
    public Guid? ResultId { get; set; }
    public ReflectionGenerationStatus Status { get; set; } = ReflectionGenerationStatus.Queued;
    public ReflectionCancellationReason? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentReflectionQuota
{
    public Guid BotId { get; set; }
    public DateOnly Date { get; set; }
    public string TimeZoneId { get; set; } = "";
    public int UsedNormal { get; set; }
    public int UsedImportant { get; set; }
}

public sealed class AgentReflectionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SessionId { get; set; }
    public long RawBoundary { get; set; }
    public Guid GenerationId { get; set; }
    public Guid OperationId { get; set; }
    public ReflectionPriority Priority { get; set; } = ReflectionPriority.Normal;
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}