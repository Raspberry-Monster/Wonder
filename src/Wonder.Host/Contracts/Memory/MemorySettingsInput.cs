namespace Wonder.Host.Contracts;

public sealed record MemorySettingsInput(
    bool AllowMemory,
    string? PreferredName = null,
    bool? AllowMemoryMerge = null,
    bool? AllowMemoryUpdate = null,
    bool? AllowMemoryArchive = null,
    bool? RetainMemoryHistory = null);