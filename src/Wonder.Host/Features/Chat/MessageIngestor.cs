using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;
using Wonder.Host.Features.Chat;

namespace Wonder.Host;

public sealed class MessageIngestor(IDbContextFactory<WonderDbContext> factory, ConversationClock clock,
    ModelRouter models, ContextBuilder contexts, InteractionBoundary tools)
{
    public async Task IngestAsync(Guid bindingId, NormalizedEvent e, CancellationToken ct)
    {
        // A short serializable transaction keeps session creation and the durable event together.
        for (var attempt = 0;; attempt++)
            try
            {
                await IngestOnceAsync(bindingId, e, ct);
                return;
            }
            catch (Exception ex) when (attempt < 8 && InteractionBoundary.IsRetryable(ex))
            {
                await Task.Delay(Random.Shared.Next(10, 30) * (attempt + 1), ct);
            }
    }

    private async Task IngestOnceAsync(Guid bindingId, NormalizedEvent e, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var binding = await db.Bindings.SingleAsync(x => x.Id == bindingId && x.Enabled, ct);
        if (binding.PlatformAccountId != e.AccountId) throw new InvalidOperationException("OneBot account mismatch.");
        var bot = await db.Bots.SingleAsync(x => x.Id == binding.BotId, ct);
        if (!bot.Enabled || e.SenderId == e.AccountId) return;
        if (await db.Messages.AnyAsync(
                x => x.BotId == bot.Id && x.Platform == "qq" && x.ExternalMessageId == e.MessageId, ct)) return;
        if (await db.ForceReceipts.AnyAsync(x => x.BotId == bot.Id && x.PlatformEventKey == e.MessageId, ct)) return;
        var identity =
            await db.PlatformIdentities.SingleOrDefaultAsync(x => x.Platform == "qq" && x.ExternalId == e.SenderId, ct);
        LogicalUser user;
        if (identity is null)
        {
            user = new LogicalUser();
            db.LogicalUsers.Add(user);
            db.PlatformIdentities.Add(new PlatformIdentity { UserId = user.Id, ExternalId = e.SenderId });
        }
        else
        {
            user = await db.LogicalUsers.SingleAsync(x => x.Id == identity.UserId, ct);
        }

        var text = string.Concat(e.Segments.Where(x => x.Kind == SegmentKind.Text).Select(x => x.Value)).Trim();
        if (e.Kind == ConversationKind.Private && text.StartsWith("/bind ", StringComparison.Ordinal))
        {
            var hash = Json.Hash(text[6..]);
            var challenge =
                await db.BindingChallenges.SingleOrDefaultAsync(
                    x => x.CodeHash == hash && x.ExpiresAt > DateTimeOffset.UtcNow, ct);
            if (challenge is not null)
            {
                var appUser = await db.Users.SingleAsync(x => x.Id == challenge.ApplicationUserId, ct);
                if (appUser.LogicalUserId is null && !await db.Users.AnyAsync(x => x.LogicalUserId == user.Id, ct))
                {
                    appUser.LogicalUserId = user.Id;
                    db.BindingChallenges.Remove(challenge);
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var policy = e.Kind == ConversationKind.Group
            ? await db.GroupPolicies.SingleOrDefaultAsync(x => x.BotId == bot.Id && x.GroupId == e.ConversationId, ct)
            : null;
        var replyIds = e.Segments.Where(x => x.Kind == SegmentKind.ReplyReference).Select(x => x.Value).ToArray();
        var repliesToBot = replyIds.Length > 0 && await (from part in db.Parts
            join batch in db.Batches on part.BatchId equals batch.Id
            join conversation in db.Conversations on batch.ConversationId equals conversation.Id
            where batch.BotId == bot.Id && conversation.Kind == e.Kind && conversation.ExternalId == e.ConversationId &&
                  part.ExternalId != null && replyIds.Contains(part.ExternalId)
            select part.Id).AnyAsync(ct);
        var decision = OneBotProtocol.Decide(e, bot, policy, repliesToBot, user.AllowMemory);
        if (!decision.ShouldPersist && !UserCommands.IsHelp(text) && !UserCommands.IsForce(text))
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        var conv = await db.Conversations.SingleOrDefaultAsync(
            x => x.BotId == bot.Id && x.Kind == e.Kind && x.ExternalId == e.ConversationId, ct);
        if (conv is null)
        {
            conv = new Conversation { BotId = bot.Id, ExternalId = e.ConversationId, Kind = e.Kind };
            db.Conversations.Add(conv);
        }

        var now = await InteractionBoundary.DatabaseNowAsync(db, clock.UtcNow, ct);
        var isHelp = UserCommands.IsHelp(text);
        var isForce = UserCommands.IsForce(text);
        var mute = await db.Mutes.Where(x => x.ConversationId == conv.Id && x.EndedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.StartedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        string? systemReply = isHelp ? UserCommands.Help(e.Kind, policy) : null;
        var bound = await db.Users.AnyAsync(x => x.LogicalUserId == user.Id, ct);
        if (isForce)
        {
            systemReply = e.Kind != ConversationKind.Private || !bound
                ? "[Wonder 系统通知] /force 仅限已绑定的私聊用户使用。"
                : mute is null ? "[Wonder 系统通知] 当前未静音；未扣次数，请直接发送消息。"
                : !decision.ShouldPersist ? "[Wonder 系统通知] 当前未启用消息保存，无法受理 Force；未扣次数。"
                : mute.UserId != user.Id ? "[Wonder 系统通知] 你无权使用该静音周期的 Force。"
                : mute.ForceRemaining <= 0 ? "[Wonder 系统通知] 本轮 Force 次数已用完；请等待静音到期。/help 仍可用。"
                : text.Length <= 6 ? "[Wonder 系统通知] 用法：/force <完整内容>；未扣次数。" : null;
            if (systemReply is null)
            {
                var profile = await models.ProfileAsync(bot.Id, TaskKind.Chat, ct);
                if (e.Segments.Count(x => x.Kind == SegmentKind.Image) > 3 ||
                    (e.Segments.Any(x => x.Kind == SegmentKind.Image) && !profile.SupportsImages))
                    systemReply = "[Wonder 系统通知] 当前模型无法完整处理这些图片；Force 未受理、未扣次数。";
                else
                {
                    var identityText = await db.Identities.Where(x => x.BotId == bot.Id).OrderByDescending(x => x.Revision)
                        .Select(x => x.Content).FirstAsync(ct);
                    var mood = await db.Emotions.AsNoTracking().SingleAsync(x => x.BotId == bot.Id, ct);
                    var definitions = profile.SupportsTools ? ConversationTools.Definitions
                        .Concat(tools.Enabled ? InteractionBoundary.Definitions : [])
                        .Concat(tools.ReflectionEnabled ? InteractionBoundary.ReflectionDefinitions : [])
                        .Concat(tools.NotesEnabled && user.AllowNotes ? InteractionBoundary.NotesDefinitions : []).ToList() : null;
                    try
                    {
                        contexts.Build(new ContextInput(identityText, mood, "", "", [], [],
                            new ContextTurn("user", $"[Server: authorized Force input; remaining at admission={mute!.ForceRemaining - 1}. " +
                                "Replying does not unmute. You may explicitly choose interaction_no_reply.]\n" +
                                string.Concat(e.Segments.Where(x => x.Kind == SegmentKind.Text).Select(x => x.Value)),
                                e.Segments, SpeakerTags.For(conv.Id, user.Id), user.PreferredName,
                                SentAt: clock.Local(e.OccurredAt)), models.Context(profile), TimeContext:
                            clock.Context(e.OccurredAt, null, false), Tools: definitions, SupportsImages: profile.SupportsImages));
                    }
                    catch (ContextLimitException)
                    {
                        systemReply = "[Wonder 系统通知] 完整 Force 内容超出当前模型上下文，未受理、未扣次数；请分段发送。";
                    }
                }
            }
        }

        if (systemReply is not null || (mute is not null && !isForce))
        {
            // No blocked content is stored at all: history, quotes, summary and reflection cannot recover it.
            var marker = new ChatMessage
            {
                BotId = bot.Id, ConversationId = conv.Id, SenderId = user.Id, ExternalMessageId = e.MessageId,
                ContextInvalidated = true, CreatedAt = e.OccurredAt, ExpiresAt = now.AddDays(bot.MessageRetentionDays)
            };
            db.Messages.Add(marker);
            if (systemReply is not null) UserCommands.QueueSystem(db, bot.Id, conv.Id, marker.Id, systemReply);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        ConversationSession? session = null;
        if (decision.ShouldRespond)
        {
            session = await db.Sessions.SingleOrDefaultAsync(
                x => x.ConversationId == conv.Id && x.Status == SessionStatus.Active, ct);
            if (session is null)
            {
                session = new ConversationSession
                {
                    BotId = bot.Id, ConversationId = conv.Id,
                    StartingMoodRevision = await db.Emotions.Where(x => x.BotId == bot.Id).Select(x => x.Revision)
                        .SingleAsync(ct)
                };
                db.Sessions.Add(session);
            }

            session.LastActivityAt = now;
            session.DeadlineAt = session.LastActivityAt.AddMinutes(e.Kind == ConversationKind.Private
                ? bot.PrivateIdleMinutes
                : bot.GroupIdleMinutes);
            session.Revision++;
        }

        var message = new ChatMessage
        {
            BotId = bot.Id,
            ConversationId = conv.Id,
            SessionId = session?.Id,
            SenderId = user.Id,
            ExternalMessageId = e.MessageId,
            SegmentsJson = Json.Write(e.Segments),
            Text = isForce ? string.Concat(e.Segments.Where(x => x.Kind == SegmentKind.Text).Select(x => x.Value)) : text,
            CreatedAt = e.OccurredAt,
            AllowReflection = decision.ShouldReflect,
            ShouldRespond = decision.ShouldRespond,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(bot.MessageRetentionDays)
        };
        db.Messages.Add(message);
        if (decision.ShouldRespond)
        {
            var chat = Jobs.Add(db, bot.Id, JobTypes.Chat, new IdPayload(message.Id), conv.Id);
            if (isForce && mute is not null)
            {
                mute.ForceRemaining--;
                mute.Revision++;
                db.ForceReceipts.Add(new ForceReceipt
                {
                    BotId = bot.Id, MuteSessionId = mute.Id, UserId = user.Id, PlatformEventKey = e.MessageId,
                    MessageId = message.Id, ChatJobId = chat.Id, ChargeGeneration = mute.ResetGeneration,
                    RemainingAfter = mute.ForceRemaining, AcceptedAt = now
                });
                UserCommands.QueueSystem(db, bot.Id, conv.Id, message.Id,
                    $"[Wonder 系统通知] Force 已受理，剩余 {mute.ForceRemaining} 次；Miku 可以回复或选择不回复。");
            }
        }
        await db.SaveChangesAsync(ct);
        if (decision.ShouldReflect && session is not null)
            Jobs.Add(db, bot.Id, JobTypes.Reflection,
                new ReflectionJob(session.Id, user.Id, message.Sequence, user.RevocationEpoch, "light"), conv.Id);
        if (UserCommands.IsEnd(text) && session is not null)
        {
            session.DeadlineAt = DateTimeOffset.UtcNow;
            session.Revision++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
