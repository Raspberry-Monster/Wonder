using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class ShareMemoryInputValidator : AbstractValidator<ShareMemoryInput>
{
    public ShareMemoryInputValidator()
    {
        RuleFor(x => x.Scope).Must(x => x is MemoryScope.UserShared or MemoryScope.SpaceShared);
        RuleFor(x => x.ContextSpaceId).NotNull().NotEqual(Guid.Empty).When(x => x.Scope == MemoryScope.SpaceShared);
    }
}