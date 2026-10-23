using System.Text;
using System.Text.Json;
using Wonder.Core;

namespace Wonder.UnitTests;

public sealed class PromptJsonTests
{
    [Test]
    public async Task PromptJsonKeepsChineseReadableAndPreservesStringContents()
    {
        var text = "树莓：明天讨论笔记本。\n路径 C:\\notes；原文 \\u4F60；\"引用\" <tag> 🌙";
        var prompt = Json.ForPrompt(new { text });
        await Assert.That(prompt).Contains("树莓：明天讨论笔记本。");
        using var parsed = JsonDocument.Parse(prompt);
        await Assert.That(parsed.RootElement.GetProperty("text").GetString()).IsEqualTo(text);
        await Assert.That(Encoding.UTF8.GetByteCount(prompt))
            .IsLessThan(Encoding.UTF8.GetByteCount(Json.Write(new { text })));
    }

    [Test]
    public async Task StoredEscapedJsonIsDecodedBeforePromptSerialization()
    {
        const string stored = "[\"\\u559C\\u6B22\\u7B80\\u77ED\\u56DE\\u590D\",\"literal \\\\u4F60\"]";
        var preferences = Json.Read<string[]>(stored);
        var prompt = Json.ForPrompt(preferences);
        await Assert.That(prompt).Contains("喜欢简短回复");
        await Assert.That(Json.Read<string[]>(prompt)[1]).IsEqualTo("literal \\u4F60");
        // Stored payload serialization also participates in existing deduplication hashes.
        await Assert.That(Json.Write(preferences)).IsEqualTo(stored);
    }
}