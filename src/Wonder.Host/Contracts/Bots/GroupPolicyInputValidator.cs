using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class GroupPolicyInputValidator : AbstractValidator<GroupPolicyInput>
{
    public GroupPolicyInputValidator()
    {
        RuleFor(x => x.CommandPrefix).NotNull().MaximumLength(50);
    }
}
