using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonder.Core;
using Wonder.Infrastructure;

namespace Wonder.IntegrationTests;

public sealed class SpeakerContextTests
{
    [Test]
    public async Task GroupMessagesKeepDistinctAuthorsAcrossHistoryQuotesAndNameChanges()
    {
        using var app = new TestApp();
        var (botId, bindingId) = await CreateBotAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var ingestor = scope.ServiceProvider.GetRequiredService<MessageIngestor>();
        async Task Ingest(string messageId, string sender, string text, bool quote = false) =>
            await ingestor.IngestAsync(bindingId,
                new NormalizedEvent("123", messageId, sender, "789", ConversationKind.Group,
                    quote ? [new MessageSegment(SegmentKind.Mention, "123"), new MessageSegment(SegmentKind.Text, text),
                        new MessageSegment(SegmentKind.ReplyReference, "1")] :
                        [new MessageSegment(SegmentKind.Mention, "123"), new MessageSegment(SegmentKind.Text, text)],
                    DateTimeOffset.UtcNow), default);
        await Ingest("1", "456", "我叫小明，我喜欢蓝色。");
        await Ingest("2", "654", "我也叫小明，但我喜欢红色。", true);
        var first = await db.Messages.AsNoTracking().SingleAsync(x => x.BotId == botId && x.ExternalMessageId == "1");
        var second = await db.Messages.AsNoTracking().SingleAsync(x => x.BotId == botId && x.ExternalMessageId == "2");
        var firstTag = SpeakerTags.For(first.ConversationId, first.SenderId);
        var secondTag = SpeakerTags.For(second.ConversationId, second.SenderId);
        await Assert.That(firstTag).IsNotEqualTo(secondTag);
        var workflow = scope.ServiceProvider.GetRequiredService<ChatWorkflow>();
        var outgoing = scope.ServiceProvider.GetRequiredService<OutgoingWorkflow>();
        var jobs = await db.Outbox.Where(x => x.BotId == botId && x.Type == JobTypes.Chat).OrderBy(x => x.Sequence).ToListAsync();
        await workflow.RunAsync(jobs[0].Id, default);
        await outgoing.RunAsync((await db.Outbox.SingleAsync(x => x.BotId == botId && x.Type == JobTypes.Outgoing)).Id, default);
        await workflow.RunAsync(jobs[1].Id, default);
        using var secondRequest = JsonDocument.Parse(app.ModelRequests[1]);
        var messages = secondRequest.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var current = messages[^1].GetProperty("content").GetString()!;
        await Assert.That(current.StartsWith($"[Message author: {secondTag}]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(current).Contains("我也叫小明");
        await Assert.That(current).Contains($"\"speaker\":\"{firstTag}\"");
        await Assert.That(messages.Any(m => m.GetProperty("role").GetString() == "user" &&
            m.GetProperty("content").GetString()!.StartsWith($"[Message author: {firstTag}]", StringComparison.Ordinal))).IsTrue();
        await Assert.That(messages.Any(m => m.GetProperty("role").GetString() == "assistant" &&
            m.GetProperty("content").GetString()!.EndsWith("这是上一轮的回复。", StringComparison.Ordinal))).IsTrue();
        await Assert.That(app.ModelRequests[1]).DoesNotContain(first.SenderId!.Value.ToString());
        await Assert.That(app.ModelRequests[1]).DoesNotContain(second.SenderId!.Value.ToString());
        await outgoing.RunAsync((await db.Outbox.SingleAsync(x => x.BotId == botId && x.Type == JobTypes.Outgoing &&
            x.Status == JobStatus.Pending)).Id, default);
        await Ingest("3", "456", "我现在改名叫小蓝了。");
        var third = await db.Messages.AsNoTracking().SingleAsync(x => x.BotId == botId && x.ExternalMessageId == "3");
        var thirdJob = await db.Outbox.Where(x => x.BotId == botId && x.Type == JobTypes.Chat).OrderByDescending(x => x.Sequence).FirstAsync();
        await workflow.RunAsync(thirdJob.Id, default);
        using var thirdRequest = JsonDocument.Parse(app.ModelRequests[2]);
        var renamed = thirdRequest.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
        await Assert.That(third.SenderId).IsEqualTo(first.SenderId);
        await Assert.That(renamed.StartsWith($"[Message author: {firstTag}]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(renamed).Contains("改名叫小蓝");
    }

    [Test]
    public async Task RollingSummaryReceivesSpeakerLabelsWithoutLocalUserIds()
    {
        using var app = new TestApp();
        var (botId, _) = await CreateBotAsync(app);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var a = new LogicalUser();
        var b = new LogicalUser();
        var conversation = new Conversation { BotId = botId, Kind = ConversationKind.Group, ExternalId = "group-summary" };
        var session = new ConversationSession { BotId = botId, ConversationId = conversation.Id };
        db.LogicalUsers.AddRange(a, b);
        db.Conversations.Add(conversation);
        db.Sessions.Add(session);
        var rows = Enumerable.Range(0, 26).Select(i => new ChatMessage
        {
            BotId = botId, ConversationId = conversation.Id, SessionId = session.Id,
            SenderId = i % 2 == 0 ? a.Id : b.Id, ExternalMessageId = "summary-" + i,
            Text = "message " + i, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        }).ToArray();
        db.Messages.AddRange(rows);
        await db.SaveChangesAsync();
        var job = Jobs.Add(db, botId, JobTypes.Summary, new SummaryJob(session.Id, rows.Max(x => x.Sequence)));
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<MaintenanceWorkflow>().RunAsync(job.Id, default);
        using var request = JsonDocument.Parse(app.ModelRequests.Single());
        var data = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using var summaryInput = JsonDocument.Parse(data);
        var speakers = summaryInput.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("speaker").GetString()).Distinct().ToArray();
        await Assert.That(speakers.Length).IsEqualTo(2);
        await Assert.That(speakers).Contains(SpeakerTags.For(conversation.Id, a.Id));
        await Assert.That(speakers).Contains(SpeakerTags.For(conversation.Id, b.Id));
        await Assert.That(data).DoesNotContain(a.Id.ToString());
        await Assert.That(data).DoesNotContain(b.Id.ToString());
    }

    private static async Task<(Guid BotId, Guid BindingId)> CreateBotAsync(TestApp app)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WonderDbContext>();
        var owner = new ApplicationUser { Id = Guid.NewGuid() };
        var bot = new BotInstance { OwnerId = owner.Id };
        var binding = new PlatformBinding { BotId = bot.Id, PlatformAccountId = "123" };
        db.Users.Add(owner);
        db.Bots.Add(bot);
        db.Bindings.Add(binding);
        db.Emotions.Add(new GlobalEmotionState { BotId = bot.Id });
        db.Identities.Add(new PersonaIdentityRevision { BotId = bot.Id, Revision = 1, Content = "你叫 Wonder。" });
        db.GroupPolicies.Add(new GroupPolicy { BotId = bot.Id, GroupId = "789", RespondToMention = true });
        var key = scope.ServiceProvider.GetRequiredService<SecretProtector>().Protect("test-key");
        foreach (var task in new[] { TaskKind.Chat, TaskKind.RollingSummary })
            db.Models.Add(new ModelProfile { BotId = bot.Id, TaskKind = task, Endpoint = "https://model.example/v1/",
                Model = "test-model", ProtectedApiKey = key });
        await db.SaveChangesAsync();
        return (bot.Id, binding.Id);
    }
}
