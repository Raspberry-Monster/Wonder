using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed class BindingInputValidator : AbstractValidator<BindingInput>
{
    public BindingInputValidator()
    {
        RuleFor(x => x.PlatformAccountId).Must(x => long.TryParse(x, out var id) && id > 0);
        RuleFor(x => x.WebSocketUrl).Must(x => RequestRules.IsUrl(x, "ws", "wss"));
    }
}
