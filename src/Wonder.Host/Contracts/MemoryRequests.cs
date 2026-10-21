namespace Wonder.Host.Contracts;

public sealed record MemorySettingsInput(bool AllowMemory, bool? AllowCrossGroupMemory = null, string? PreferredName = null,
    bool? AllowMemoryMerge = null, bool? AllowMemoryUpdate = null, bool? AllowMemoryArchive = null, bool? RetainMemoryHistory = null);

public sealed record ShareMemoryInput(Wonder.Core.MemoryScope Scope, Guid? ContextSpaceId = null);

public sealed record ContextSpaceInput(string Name);

public sealed record CorrectMemory(string Text);
