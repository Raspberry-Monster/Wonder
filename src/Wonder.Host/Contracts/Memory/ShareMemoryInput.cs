using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed record ShareMemoryInput(MemoryScope Scope, Guid? ContextSpaceId = null);