using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class PublishIdentityValidator : AbstractValidator<PublishIdentity>
{
    public PublishIdentityValidator()
    {
        RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
    }
}