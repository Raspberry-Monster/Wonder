using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wonder.Core;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/bots/{id:guid}/model-routes")]
public sealed class ModelRoutesController(WonderDbContext db, SecretProtector secrets) : WonderControllerBase(db)
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        return Ok(await Db.Models.AsNoTracking().Where(x => x.BotId == id).OrderBy(x => x.TaskKind).Select(x =>
            new
            {
                x.Id, x.TaskKind, x.Endpoint, x.Model, x.ContextWindow, x.MaxOutputTokens, x.SupportsImages,
                x.SupportsJson, x.SupportsTools, x.ThinkingEnabled, x.ReasoningEffort, x.EmbeddingDimensions,
                x.InputCostPerMillion, x.OutputCostPerMillion,
                hasApiKey = x.ProtectedApiKey != ""
            }).ToListAsync(ct));
    }

    [HttpPut]
    public async Task<IActionResult> Update(Guid id, ModelRouteInput[] inputs, CancellationToken ct)
    {
        if (await FindOwnedBotAsync(id, ct) is null) return NotFound();
        if (inputs.Length > 7 || inputs.Select(x => x.TaskKind).Distinct().Count() != inputs.Length)
            return BadRequest();
        foreach (var input in inputs)
        {
            if (input.SupportsTools == true && input.TaskKind != TaskKind.Chat) return BadRequest();
            if (!Enum.IsDefined(input.TaskKind) || !Uri.TryCreate(input.Endpoint, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
                string.IsNullOrWhiteSpace(input.Model) || input.ContextWindow is < 2048 or > 2_000_000 ||
                input.MaxOutputTokens is < 16 or > 32768 || input.MaxOutputTokens + 1024 >= input.ContextWindow ||
                input.EmbeddingDimensions is < 1 or > 8192 || input.InputCostPerMillion < 0 ||
                input.OutputCostPerMillion < 0 ||
                input.ReasoningEffort is not (null or "low" or "high" or "max") ||
                input.ReasoningEffort is not null && input.ThinkingEnabled != true ||
                input.TaskKind == TaskKind.Embedding &&
                (input.ThinkingEnabled is not null || input.ReasoningEffort is not null)) return BadRequest();
            var profile = await Db.Models.SingleOrDefaultAsync(x => x.BotId == id && x.TaskKind == input.TaskKind, ct);
            if (profile is null)
            {
                if (string.IsNullOrWhiteSpace(input.ApiKey)) return BadRequest();
                profile = new ModelProfile { BotId = id, TaskKind = input.TaskKind };
                Db.Models.Add(profile);
            }

            var embeddingChanged = profile.TaskKind == TaskKind.Embedding && (profile.Endpoint != input.Endpoint ||
                                                                              profile.Model != input.Model ||
                                                                              profile.EmbeddingDimensions !=
                                                                              input.EmbeddingDimensions);
            profile.Endpoint = input.Endpoint;
            profile.Model = input.Model;
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
                foreach (var memory in memoryIds) Jobs.Add(Db, id, JobTypes.Embedding, new IdPayload(memory));
            }
        }

        await Db.SaveChangesAsync(ct);
        return NoContent();
    }
}