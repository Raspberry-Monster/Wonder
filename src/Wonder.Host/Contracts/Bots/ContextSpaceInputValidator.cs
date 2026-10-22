using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class ContextSpaceInputValidator : AbstractValidator<ContextSpaceInput>
{
    public ContextSpaceInputValidator() => RuleFor(x => x.Name).NotEmpty().Must(x => x is not null && x.Trim().Length <= 100);
}
