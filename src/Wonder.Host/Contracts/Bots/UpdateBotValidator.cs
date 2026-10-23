using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class UpdateBotValidator : AbstractValidator<UpdateBot>
{
    public UpdateBotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100).When(x => x.Name is not null);
        RuleFor(x => x.MessageRetentionDays).InclusiveBetween(1, 3650);
        RuleFor(x => x.PrivateIdleMinutes).InclusiveBetween(1, 1440);
        RuleFor(x => x.GroupIdleMinutes).InclusiveBetween(1, 1440);
        RuleFor(x => x.DailyTokenLimit).GreaterThan(0);
        RuleFor(x => x.MonthlyTokenLimit).GreaterThan(0);
    }
}