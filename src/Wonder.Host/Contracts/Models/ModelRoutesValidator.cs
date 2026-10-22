using FluentValidation;
using Wonder.Core;

namespace Wonder.Host.Contracts;

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
