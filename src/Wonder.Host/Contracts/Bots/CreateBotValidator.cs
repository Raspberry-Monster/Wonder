using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class CreateBotValidator : AbstractValidator<CreateBot>
{
    public CreateBotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Identity).NotEmpty().MaximumLength(4000);
    }
}