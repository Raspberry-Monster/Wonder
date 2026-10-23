using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class MemorySettingsInputValidator : AbstractValidator<MemorySettingsInput>
{
    public MemorySettingsInputValidator()
    {
        RuleFor(x => x.PreferredName)
            .Must(x => x is null || x.Trim().Length <= 100);
    }
}