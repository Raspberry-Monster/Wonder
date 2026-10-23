using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class DeliveryResolutionValidator : AbstractValidator<DeliveryResolution>
{
    public DeliveryResolutionValidator()
    {
        RuleFor(x => x.Action).Must(x => x is "confirm-sent" or "retry");
    }
}