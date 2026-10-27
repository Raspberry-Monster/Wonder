using Microsoft.Extensions.AI;
using Wonder.Core;
using Wonder.Infrastructure.Models;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Wonder.UnitTests;

public sealed class TokenCounterTests
{
    [Test]
    public async Task StandardReasoningContentIsIncludedInTheMessageBudget()
    {
        using var counter = new TokenCounter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var profile = new ModelProfile { TokenizerId = "estimate" };
        var message = new AiMessage(ChatRole.Assistant, "answer");
        var plain = counter.Messages(profile, [message]);
        message.Contents.Add(new TextReasoningContent("internal reasoning"));
        await Assert.That(counter.Messages(profile, [message]) - plain).IsEqualTo(18);
    }

    [Test]
    public async Task DirectoryVocabulariesAreDiscoveredAndAppliedByModelAliasOrExplicitChoice()
    {
        var directory = Directory.CreateTempSubdirectory("wonder-tokenizers-");
        try
        {
            var vocabulary = directory.CreateSubdirectory("test-vocabulary");
            File.WriteAllText(Path.Combine(vocabulary.FullName, "tokenizer.json"), """
                {"version":"1.0","added_tokens":[],"pre_tokenizer":{"type":"Whitespace"},
                 "model":{"type":"WordLevel","vocab":{"[UNK]":0,"Hello":1,"world":2},"unk_token":"[UNK]"}}
                """);
            File.WriteAllText(Path.Combine(vocabulary.FullName, "models.json"),
                """["my-model", "provider/my-model"]""");
            directory.CreateSubdirectory("not-a-tokenizer");
            using var counter = new TokenCounter(directory.FullName);
            await Assert.That(counter.Available).HasSingleItem();
            await Assert.That(counter.Available[0].Id).IsEqualTo("test-vocabulary");
            foreach (var model in new[] { "test-vocabulary", "my-model", "PROVIDER/MY-MODEL" })
            {
                var profile = new ModelProfile { Model = model };
                await Assert.That(counter.Resolve(profile)).IsEqualTo("test-vocabulary");
                await Assert.That(counter.Text(profile, "Hello world")).IsEqualTo(2);
            }

            await Assert.That(counter.Text(new ModelProfile { Model = "unrelated", TokenizerId = "test-vocabulary" },
                    "Hello world"))
                .IsEqualTo(2);
            await Assert.That(counter.Text(new ModelProfile { Model = "my-model", TokenizerId = "estimate" },
                    "Hello world"))
                .IsEqualTo(11);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Test]
    public async Task UnknownModelUsesEstimateAndUnknownExplicitTokenizerIsReported()
    {
        using var counter = new TokenCounter(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var profile = new ModelProfile { Model = "my-local-model" };
        await Assert.That(counter.Resolve(profile)).IsEqualTo("estimate");
        await Assert.That(counter.Text(profile, "hello")).IsEqualTo(5);
        await Assert.That(() => counter.Text(new ModelProfile { TokenizerId = "missing" }, "hello"))
            .Throws<ModelConfigurationException>();
    }
}
