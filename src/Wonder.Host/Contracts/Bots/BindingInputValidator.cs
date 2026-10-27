using FluentValidation;
using Wonder.Host.Milky;

namespace Wonder.Host.Contracts;

public sealed class BindingInputValidator : AbstractValidator<BindingInput>
{
    public BindingInputValidator()
    {
        RuleFor(x => x.PlatformAccountId).Matches(BindingRegex.PlatformAccountId())
            .Must(x => long.TryParse(x, out var id) && id > 0);
        RuleFor(x => x.ApiBaseUrl).Must(MilkyConnection.IsValidBaseUrl)
            .WithMessage("请填写 Milky 服务的 HTTP(S) 基础地址，不包含 /api 或 /event、查询参数及凭据。");
        RuleFor(x => x.AccessToken).Must(x => x is null || !x.Any(char.IsControl))
            .WithMessage("访问令牌不能包含控制字符。");
        RuleFor(x => x.ClearAccessToken).Must((input, clear) => !clear || string.IsNullOrEmpty(input.AccessToken))
            .WithMessage("清除令牌时请留空访问令牌。");
    }
}