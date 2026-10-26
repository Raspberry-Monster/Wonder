using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed record ErasePreviewInput(long ExpectedRevision, Guid[]? MessageIds = null);
public sealed record ConfirmEraseInput(Guid PreviewId, long ExpectedRevision, string ScopeHash);
public sealed class ErasePreviewInputValidator : AbstractValidator<ErasePreviewInput>
{
    public ErasePreviewInputValidator()
    {
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.MessageIds).Must(x => x is null || x.Length <= 100);
    }
}
public sealed class ConfirmEraseInputValidator : AbstractValidator<ConfirmEraseInput>
{
    public ConfirmEraseInputValidator()
    {
        RuleFor(x => x.PreviewId).NotEmpty(); RuleFor(x => x.ExpectedRevision).GreaterThan(0);
        RuleFor(x => x.ScopeHash).NotEmpty().Length(64);
    }
}
