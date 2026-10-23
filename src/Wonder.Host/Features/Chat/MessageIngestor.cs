using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.Host;

public sealed class MessageIngestor(IDbContextFactory<WonderDbContext> factory)
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
            catch (Exception ex) when (attempt < 3 && (ex is DbUpdateConcurrencyException ||
                                                       ex.InnerException is PostgresException
                                                       {
                                                           SqlState: "40001" or "23505"
                                                       } || ex is PostgresException { SqlState: "40001" }))
            {
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
        if (!decision.ShouldPersist)
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

            session.LastActivityAt = DateTimeOffset.UtcNow;
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
            Text = text,
            CreatedAt = e.OccurredAt,
            AllowReflection = decision.ShouldReflect,
            ShouldRespond = decision.ShouldRespond,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(bot.MessageRetentionDays)
        };
        db.Messages.Add(message);
        if (decision.ShouldRespond) Jobs.Add(db, bot.Id, JobTypes.Chat, new IdPayload(message.Id), conv.Id);
        await db.SaveChangesAsync(ct);
        if (decision.ShouldReflect && session is not null)
            Jobs.Add(db, bot.Id, JobTypes.Reflection,
                new ReflectionJob(session.Id, user.Id, message.Sequence, user.RevocationEpoch, "light"), conv.Id);
        if (text is "/end" or "/结束" && session is not null)
        {
            session.DeadlineAt = DateTimeOffset.UtcNow;
            session.Revision++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}