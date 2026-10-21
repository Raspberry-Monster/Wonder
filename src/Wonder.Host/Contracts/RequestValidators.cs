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

public sealed class CreateBotValidator : AbstractValidator<CreateBot>
{
    public CreateBotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Identity).NotEmpty().MaximumLength(4000);
    }
}

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

public sealed class PublishIdentityValidator : AbstractValidator<PublishIdentity>
{
    public PublishIdentityValidator() => RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
}

public sealed class BindingInputValidator : AbstractValidator<BindingInput>
{
    public BindingInputValidator()
    {
        RuleFor(x => x.PlatformAccountId).Must(x => long.TryParse(x, out var id) && id > 0);
        RuleFor(x => x.WebSocketUrl).Must(x => RequestRules.IsUrl(x, "ws", "wss"));
    }
}

public sealed class ModelRouteInputValidator : AbstractValidator<ModelRouteInput>
{
    public ModelRouteInputValidator()
    {
        RuleFor(x => x.TaskKind).IsInEnum();
        RuleFor(x => x.Endpoint).Must(x => RequestRules.IsUrl(x, "http", "https"));
        RuleFor(x => x.Model).NotEmpty();
        RuleFor(x => x.ContextWindow).InclusiveBetween(2048, 2_000_000);
        RuleFor(x => x.MaxOutputTokens).InclusiveBetween(16, 32768)
            .Must((x, max) => max + 1024 < x.ContextWindow);
        RuleFor(x => x.EmbeddingDimensions).InclusiveBetween(1, 8192);
        RuleFor(x => x.InputCostPerMillion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.OutputCostPerMillion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.SupportsTools).Must((x, tools) => tools != true || x.TaskKind == TaskKind.Chat);
        RuleFor(x => x.ReasoningEffort).Must(x => x is null or "low" or "high" or "max")
            .Must((x, effort) => effort is null || x.ThinkingEnabled == true);
        RuleFor(x => x.ThinkingEnabled).Null().When(x => x.TaskKind == TaskKind.Embedding);
        RuleFor(x => x.ReasoningEffort).Null().When(x => x.TaskKind == TaskKind.Embedding);
    }
}

public sealed class ModelRoutesValidator : AbstractValidator<ModelRouteInput[]>
{
    public ModelRoutesValidator()
    {
        RuleFor(x => x).Must(x => x.Length <= 7).WithMessage("At most seven routes are allowed.")
            .Must(x => x.Where(v => v is not null).Select(v => v.TaskKind).Distinct().Count() == x.Length)
            .WithMessage("Task kinds must be unique and routes must not be null.");
        RuleForEach(x => x).NotNull().SetValidator(new ModelRouteInputValidator());
    }
}

public sealed class GroupPolicyInputValidator : AbstractValidator<GroupPolicyInput>
{
    public GroupPolicyInputValidator()
    {
        RuleFor(x => x.ProactiveEnabled).Equal(false).WithMessage("Proactive sending is not available in this version.");
        RuleFor(x => x.CommandPrefix).NotNull().MaximumLength(50);
    }
}

public sealed class DeliveryResolutionValidator : AbstractValidator<DeliveryResolution>
{
    public DeliveryResolutionValidator() => RuleFor(x => x.Action).Must(x => x is "confirm-sent" or "retry");
}

public sealed class MemorySettingsInputValidator : AbstractValidator<MemorySettingsInput>
{
    public MemorySettingsInputValidator() => RuleFor(x => x.PreferredName)
        .Must(x => x is null || x.Trim().Length <= 100);
}

public sealed class ShareMemoryInputValidator : AbstractValidator<ShareMemoryInput>
{
    public ShareMemoryInputValidator()
    {
        RuleFor(x => x.Scope).Must(x => x is MemoryScope.UserShared or MemoryScope.SpaceShared);
        RuleFor(x => x.ContextSpaceId).NotNull().NotEqual(Guid.Empty).When(x => x.Scope == MemoryScope.SpaceShared);
    }
}

public sealed class ContextSpaceInputValidator : AbstractValidator<ContextSpaceInput>
{
    public ContextSpaceInputValidator() => RuleFor(x => x.Name).NotEmpty().Must(x => x is not null && x.Trim().Length <= 100);
}

public sealed class CorrectMemoryValidator : AbstractValidator<CorrectMemory>
{
    public CorrectMemoryValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(1000);
}

internal static class RequestRules
{
    public static bool IsUrl(string? value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && schemes.Contains(uri.Scheme) && string.IsNullOrEmpty(uri.UserInfo);
}
