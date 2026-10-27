namespace Wonder.Core.Abstractions;

public interface IPlatformConnection
{
    Task<string> SendAsync(Guid bindingId, ConversationKind kind, string target, string text, CancellationToken ct);
}