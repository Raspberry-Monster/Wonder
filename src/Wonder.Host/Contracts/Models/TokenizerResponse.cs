namespace Wonder.Host.Contracts;

public sealed record TokenizerResponse(string Id, IReadOnlyList<string> Models);
