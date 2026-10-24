using Wonder.Core;
using Wonder.Infrastructure;
using Wonder.Platforms;

namespace Wonder.Host;

public static class UserCommands
{
    public static bool IsHelp(string text) => text == "/help";
    public static bool IsEnd(string text) => text is "/end" or "/结束";
    public static bool IsForce(string text) => text == "/force" ||
        (text.StartsWith("/force", StringComparison.Ordinal) && text.Length > 6 && char.IsWhiteSpace(text[6]));

    public static string Help(ConversationKind kind, GroupPolicy? policy) =>
        "[Wonder 系统帮助]\n" +
        "/help — 查看全部用户命令；静音期间可用，不调用 Miku、不扣次数。\n" +
        "/bind <一次性口令> — 绑定控制台账号与 QQ；仅私聊，口令由控制台生成，5 分钟有效。\n" +
        "/end 或 /结束 — 结束当前聊天分段；需允许保存消息。私聊可直接用，群聊需符合 @、回复或前缀触发条件。\n" +
        "/force <完整内容> — 仅已绑定私聊用户在静音期间使用，每轮初始 3 次；Miku 可以回复或选择不回复。未静音不扣次数。\n" +
        (kind == ConversationKind.Group && policy?.CommandPrefix is { Length: > 0 } prefix
            ? $"{prefix} <内容> — 本群配置的聊天触发前缀；需群聊保存和回复策略允许。\n" : "") +
        "Mute、Unmute、Reset、主动 Reflection、Notes 是 Miku 专用结构化工具，输入同名文字不会执行。\n" +
        "群聊仅提供基础聊天与发言者识别，不使用记忆、关系学习、反思或摘要。";

    // System receipts use durable Outgoing, but never become assistant history or open a Session.
    public static void QueueSystem(WonderDbContext db, Guid botId, Guid conversationId, Guid sourceId, string text)
    {
        var batch = new OutgoingMessageBatch
        {
            BotId = botId, ConversationId = conversationId, SourceJobId = sourceId
        };
        db.Batches.Add(batch);
        var sequence = 0;
        foreach (var part in OneBotProtocol.Split(text))
            db.Parts.Add(new OutgoingMessagePart { BatchId = batch.Id, Sequence = sequence++, Text = part });
        Jobs.Add(db, botId, JobTypes.Outgoing, new IdPayload(batch.Id), conversationId, sourceId);
    }
}
