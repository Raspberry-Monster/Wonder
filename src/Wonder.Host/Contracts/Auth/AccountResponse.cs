namespace Wonder.Host.Contracts;

public sealed record AccountResponse(Guid Id, string? Email, Guid? LogicalUserId);