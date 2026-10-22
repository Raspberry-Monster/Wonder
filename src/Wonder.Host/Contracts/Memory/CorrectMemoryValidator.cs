using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class CorrectMemoryValidator : AbstractValidator<CorrectMemory>
{
    public CorrectMemoryValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(1000);
}
