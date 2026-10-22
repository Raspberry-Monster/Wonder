using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class GroupPolicyInputValidator : AbstractValidator<GroupPolicyInput>
{
    public GroupPolicyInputValidator()
    {
        RuleFor(x => x.ProactiveEnabled).Equal(false).WithMessage("Proactive sending is not available in this version.");
        RuleFor(x => x.CommandPrefix).NotNull().MaximumLength(50);
    }
}
