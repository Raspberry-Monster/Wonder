using Wonder.Core;
using Wonder.Host;
using Wonder.Host.Features.Persona;

namespace Wonder.UnitTests;

public sealed class PromptCatalogTests
{
    [Test]
    public async Task ChatRulesAreLoadedFromTheEditableTemplate()
    {
        using var files = new PromptFiles();
        File.WriteAllText(Path.Combine(files.Directory.FullName, "ChatContext.prompt"),
            "Custom rule\nIdentity={{identity}}\nTools={{toolsAvailable}}");
        var composer = new PromptComposer(new PromptCatalog(files.Directory.FullName));
        var compiled = composer.CompileChat("Wonder", false);
        await Assert.That(compiled).Contains("Custom rule\nIdentity=Wonder\nTools=false");
        await Assert.That(compiled).DoesNotContain("Historical emotion");
    }

    [Test]
    public async Task PromptsAreCachedUntilTheNextLoadAndKeepUtf8Contents()
    {
        using var files = new PromptFiles();
        var path = Path.Combine(files.Directory.FullName, "Shared.prompt");
        const string shared = "规则：保留中文、\"引用\"和 {原文}。\n第二行";
        File.WriteAllText(path, shared + "\n");
        var prompts = new PromptCatalog(files.Directory.FullName);
        File.WriteAllText(path, "修改后的规则");

        var compiled = new PromptComposer(prompts).Compile("固定身份", new GlobalEmotionState());
        await Assert.That(prompts.Shared).IsEqualTo(shared);
        await Assert.That(compiled).StartsWith(shared + "\n\n[Identity]\n固定身份\n");
        await Assert.That(new PromptCatalog(files.Directory.FullName).Shared).IsEqualTo("修改后的规则");
    }

    [Test]
    [Arguments("Shared.prompt", null)]
    [Arguments("ChatContext.prompt", null)]
    [Arguments("SummaryProjection.prompt", null)]
    [Arguments("Reflection.prompt", "")]
    [Arguments("MemoryMaintenance.prompt", " \r\n\t")]
    public async Task MissingOrEmptyRequiredPromptsFailDuringLoading(string name, string? content)
    {
        using var files = new PromptFiles();
        var path = Path.Combine(files.Directory.FullName, name);
        if (content is null) File.Delete(path);
        else File.WriteAllText(path, content);

        var error = await Assert.That(() => new PromptCatalog(files.Directory.FullName))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains(path);
    }

    private sealed class PromptFiles : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("wonder-prompts-");

        public PromptFiles()
        {
            foreach (var file in System.IO.Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Prompts"), "*.prompt"))
                File.Copy(file, Path.Combine(Directory.FullName, Path.GetFileName(file)));
        }

        public void Dispose()
        {
            foreach (var file in Directory.GetFiles()) file.Delete();
            Directory.Delete();
        }
    }
}
