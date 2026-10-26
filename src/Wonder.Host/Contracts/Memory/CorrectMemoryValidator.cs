using FluentValidation;

namespace Wonder.Host.Contracts;

public sealed class CorrectMemoryValidator : AbstractValidator<CorrectMemory>
{
    public CorrectMemoryValidator()
    {
        RuleFor(x => x.Text).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
    }
}

public sealed class ArchiveMemoryValidator : AbstractValidator<ArchiveMemory>
{
    public ArchiveMemoryValidator()
    {
        RuleFor(x => x.ExpectedRevision).GreaterThan(0);
    }
}