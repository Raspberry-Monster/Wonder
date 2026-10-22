using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class CredentialsValidator : AbstractValidator<Credentials>
{
    public CredentialsValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(x => x.Password).NotEmpty().Length(12, 200);
    }
}
