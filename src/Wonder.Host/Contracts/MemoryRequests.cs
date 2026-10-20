namespace Wonder.Host.Contracts;

public sealed record MemorySettingsInput(bool AllowMemory, bool? AllowCrossGroupMemory = null, string? PreferredName = null);

public sealed record ShareMemoryInput(Wonder.Core.MemoryScope Scope, Guid? ContextSpaceId = null);

public sealed record ContextSpaceInput(string Name);

public sealed record CorrectMemory(string Text);