using Microsoft.Extensions.Configuration;
using Wonder.Core;
using Wonder.Infrastructure.Models;

namespace Wonder.UnitTests;

public sealed class TokenCounterTests
{
    [Test]
    public async Task OfficialDeepSeekVocabularyLoadsAndRecognizesSpecialTokens()
    {
        using var counter = new TokenCounter(new ConfigurationBuilder().Build());
        var profile = new ModelProfile { TokenizerId = TokenCounter.DeepSeekId };
        await Assert.That(counter.Text(profile, "<｜begin▁of▁sentence｜>")).IsEqualTo(1);
        await Assert.That(counter.Text(profile, "你好，Wonder！") > 0).IsTrue();
        await Assert.That(counter.Text(profile, "Hello world") < TokenCounter.Estimate("Hello world")).IsTrue();
    }

    [Test]
    public async Task CustomModelCanUseExplicitEstimateWithoutDeepSeekSettings()
    {
        using var counter = new TokenCounter(new ConfigurationBuilder().Build());
        var profile = new ModelProfile { Model = "my-local-model", TokenizerId = "estimate" };
        await Assert.That(counter.Text(profile, "hello")).IsEqualTo(5);
        await Assert.That(() => counter.Text(new ModelProfile { TokenizerId = "missing" }, "hello"))
            .Throws<ModelConfigurationException>();
    }
}
