using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;
using Wonder.Infrastructure.Models;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/model-routes")]
public sealed class ModelRoutesController(WonderDbContext db, SecretProtector secrets, TokenCounter tokenizers)
    : WonderControllerBase(db)
{
    [HttpGet("tokenizers")]
    [ProducesResponseType<TokenizerResponse[]>(200)]
    public async Task<IActionResult> GetTokenizers(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        return Ok(tokenizers.Available.Select(item => new TokenizerResponse(item.Id, item.Models)).ToArray());
    }

    [HttpGet]
    [ProducesResponseType<ModelRouteResponse[]>(200)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        var routes = await Db.Models.AsNoTracking().Where(x => x.BotId == id && x.TaskKind != TaskKind.DailyNarrative)
            .OrderBy(x => x.TaskKind).ToListAsync(ct);
        return Ok(routes.Select(x =>
            new ModelRouteResponse(
                x.Id, (ModelTaskKind)x.TaskKind, x.Endpoint, x.Model, x.ContextWindow, x.MaxOutputTokens,
                x.SupportsImages,
                x.SupportsJson, x.SupportsTools, x.ThinkingEnabled, x.ReasoningEffort, x.EmbeddingDimensions,
                x.InputCostPerMillion, x.OutputCostPerMillion, x.TokenizerId, x.MessageTokenOverhead,
                x.ImageTokenBudget,
                x.ProtectedApiKey != "", tokenizers.Resolve(x)
            )).ToArray());
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id, ModelRouteInput[] inputs, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        foreach (var input in inputs)
        {
            var profile =
                await Db.Models.SingleOrDefaultAsync(x => x.BotId == id && x.TaskKind == (TaskKind)input.TaskKind, ct);
            if (profile is null)
            {
                if (string.IsNullOrWhiteSpace(input.ApiKey)) return BadRequest();
                profile = new ModelProfile { BotId = id, TaskKind = (TaskKind)input.TaskKind };
                Db.Models.Add(profile);
            }

            var embeddingChanged = profile.TaskKind == TaskKind.Embedding && (profile.Endpoint != input.Endpoint ||
                                                                              profile.Model != input.Model ||
                                                                              profile.EmbeddingDimensions !=
                                                                              input.EmbeddingDimensions);
            profile.Endpoint = input.Endpoint;
            profile.Model = input.Model;
            profile.TokenizerId = input.TokenizerId;
            profile.MessageTokenOverhead = input.MessageTokenOverhead;
            profile.ImageTokenBudget = input.ImageTokenBudget;
            profile.ContextWindow = input.ContextWindow;
            profile.MaxOutputTokens = input.MaxOutputTokens;
            profile.SupportsImages = input.SupportsImages;
            profile.SupportsJson = input.SupportsJson;
            if (input.SupportsTools is { } supportsTools) profile.SupportsTools = supportsTools;
            profile.ThinkingEnabled = input.ThinkingEnabled;
            profile.ReasoningEffort = input.ReasoningEffort;
            profile.EmbeddingDimensions = input.EmbeddingDimensions;
            profile.InputCostPerMillion = input.InputCostPerMillion;
            profile.OutputCostPerMillion = input.OutputCostPerMillion;
            if (!string.IsNullOrEmpty(input.ApiKey)) profile.ProtectedApiKey = secrets.Protect(input.ApiKey);
            if (embeddingChanged)
            {
                var memoryIds = await Db.Memories.Where(x => x.BotId == id && x.Status != MemoryStatus.Revoked)
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Select(x => x.Id).Take(10000).ToListAsync(ct);
                foreach (var memory in memoryIds) Jobs.Add(Db, id, JobKind.Embedding, new IdPayload(memory));
            }
        }

        await Db.SaveChangesAsync(ct);
        return NoContent();
    }
}