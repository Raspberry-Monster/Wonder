using Wonder.Host;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Infrastructure.Models;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class ReflectionResponseTests
{
    [Test]
    [Arguments("", "stop", "no final content")]
    [Arguments(null, "stop", "no final content")]
    [Arguments("  ", "stop", "no final content")]
    [Arguments("{}", "length", "truncated")]
    [Arguments("", "length", "truncated")]
    [Arguments("\x60\x60\x60json\n\x60\x60\x60", "stop", "empty JSON")]
    public async Task EmptyOrTruncatedReflectionDoesNotCommitProposalsAndRetainsUsage(
        string? content, string finishReason, string expectedError)
    {
        using var app = new TestApp();
        var (jobId, botId) = await CreateJobAsync(app);
        app.ModelResponses.Enqueue(Response(content, finishReason));
        using var scope = app.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>();
        var error = await Assert.That(() => workflow.GenerateAsync(jobId, default))
            .Throws<ModelResponseException>();
        await Assert.That(error!.Message).Contains(expectedError);
        if (finishReason == "length")
        {
            await Assert.That(error.Message).Contains("LightReflection");
            await Assert.That(error.Message).Contains("FinishReason=length");
            await Assert.That(error.Message).Contains("MaxOutputTokens=1024");
        }
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        await Assert.That(await db.Reflections.Where(x => x.BotId == botId).ToListAsync()).IsEmpty();
        await Assert.That(await db.Memories.Where(x => x.BotId == botId).ToListAsync()).IsEmpty();
        await Assert.That(await db.Outbox.Where(x => x.BotId == botId && x.Id != jobId).ToListAsync()).IsEmpty();
        var usage = await db.Usage.SingleAsync(x => x.BotId == botId);
        await Assert.That(usage.OutputTokens).IsEqualTo(1024);
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == jobId)).Status).IsEqualTo(JobStatus.Pending);
    }

    [Test]
    public async Task RetriedReflectionAcceptsACompleteEmptyProposal()
    {
        using var app = new TestApp();
        var (jobId, botId) = await CreateJobAsync(app);
        app.ModelResponses.Enqueue(Response(null, "length"));
        app.ModelResponses.Enqueue(Response("{\"needsStrongReview\":false,\"emotion\":null,\"relationships\":[],\"memories\":[]}", "stop"));
        using var scope = app.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<ReflectionWorkflow>();
        await Assert.That(() => workflow.GenerateAsync(jobId, default)).ThrowsExactly<OutputLimitException>();
        await workflow.GenerateAsync(jobId, default);
        using var request = JsonDocument.Parse(app.ModelRequests[^1]);
        var content = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        await Assert.That(content).Contains("我喜欢简短回复。");
        await Assert.That(content).DoesNotContain("\\u6211");
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var result = await db.Reflections.SingleAsync(x => x.BotId == botId);
        var proposal = Json.Read<ReflectionProposal>(result.Payload);
        await Assert.That(proposal.Memories).IsEmpty();
        await Assert.That(proposal.Relationships).IsEmpty();
        await Assert.That(proposal.Emotion).IsNull();
        await Assert.That((await db.Outbox.SingleAsync(x => x.Id == jobId)).Status).IsEqualTo(JobStatus.Completed);
        await Assert.That(await db.Usage.CountAsync(x => x.BotId == botId)).IsEqualTo(2);
    }

    private static string Response(string? content, string finishReason) => Json.Write(new
    {
        choices = new[] { new { finish_reason = finishReason,
            message = new { content, reasoning_content = "not a final answer" } } },
        usage = new { prompt_tokens = 20, completion_tokens = 1024 }
    });

    private static async Task<(Guid JobId, Guid BotId)> CreateJobAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var user = new LogicalUser { AllowMemory = true };
        var conversation = new Conversation { BotId = bot.Id };
        var session = new ConversationSession { BotId = bot.Id, ConversationId = conversation.Id };
        var message = new ChatMessage { BotId = bot.Id, ConversationId = conversation.Id, SessionId = session.Id,
            SenderId = user.Id, AllowReflection = true, ExternalMessageId = "1", Text = "我喜欢简短回复。" };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        db.LogicalUsers.Add(user);
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        db.Messages.Add(message);
        db.Models.Add(new ModelProfile { BotId = bot.Id, TaskKind = TaskKind.LightReflection,
            Endpoint = "https://model.example/v1/", Model = "reflection-model", SupportsJson = true,
            ProtectedApiKey = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key") });
        await db.SaveChangesAsync();
        var job = Jobs.Add(db, bot.Id, JobTypes.Reflection,
            new ReflectionJob(session.Id, user.Id, message.Sequence, user.RevocationEpoch, "light"));
        await db.SaveChangesAsync();
        return (job.Id, bot.Id);
    }
}
