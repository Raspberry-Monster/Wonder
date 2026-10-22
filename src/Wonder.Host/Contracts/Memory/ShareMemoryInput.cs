using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed record ShareMemoryInput(Wonder.Core.MemoryScope Scope, Guid? ContextSpaceId = null);
