namespace Wonder.Host.Contracts;

public sealed record CorrectMemory(string Text, long ExpectedRevision);

public sealed record ArchiveMemory(long ExpectedRevision);
