using FluentValidation;
using Wonder.Core;
using Wonder.Infrastructure.Models;

namespace Wonder.Host.Contracts;

public sealed class ModelRouteInputValidator : AbstractValidator<ModelRouteInput>
{
    public ModelRouteInputValidator(TokenCounter tokenizers)
    {
        RuleFor(x => x.TaskKind).IsInEnum();
        RuleFor(x => x.Endpoint).Must(x => RequestRules.IsUrl(x, "http", "https"));
        RuleFor(x => x.Model).NotEmpty();
        RuleFor(x => x.TokenizerId).Cascade(CascadeMode.Stop).NotEmpty().MaximumLength(100)
            .Must(tokenizers.Contains).WithMessage("Choose auto, estimate, or an available tokenizer.");
        RuleFor(x => x.MessageTokenOverhead).InclusiveBetween(0, 4096);
        RuleFor(x => x.ImageTokenBudget).InclusiveBetween(0, 100000);
        RuleFor(x => x.ContextWindow).InclusiveBetween(2048, 2_000_000);
        RuleFor(x => x.MaxOutputTokens).InclusiveBetween(16, 1_000_000)
            .Must((x, max) => x.TaskKind == TaskKind.Embedding || max + 1024 < x.ContextWindow);
        RuleFor(x => x.EmbeddingDimensions).InclusiveBetween(1, 8192);
        RuleFor(x => x.InputCostPerMillion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.OutputCostPerMillion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.SupportsTools).Must((x, tools) => tools != true || x.TaskKind == TaskKind.Chat);
        RuleFor(x => x.ReasoningEffort).MaximumLength(32)
            .Must((x, effort) => effort is null || x.ThinkingEnabled == true);
        RuleFor(x => x.ThinkingEnabled).Null().When(x => x.TaskKind == TaskKind.Embedding);
        RuleFor(x => x.ReasoningEffort).Null().When(x => x.TaskKind == TaskKind.Embedding);
    }
}
