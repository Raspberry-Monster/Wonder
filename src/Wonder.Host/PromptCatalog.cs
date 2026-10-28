using System.Text;

namespace Wonder.Host;

public sealed class PromptCatalog
{
    public PromptCatalog(string directory)
    {
        directory = Path.GetFullPath(directory);
        Shared = Read("Shared");
        ChatContext = Read("ChatContext");
        Summary = Read("Summary");
        Reflection = Read("Reflection");
        MemoryMaintenance = Read("MemoryMaintenance");
        SummaryProjection = Read("SummaryProjection");

        string Read(string name)
        {
            var path = Path.Combine(directory, name + ".prompt");
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Cannot read required prompt file: {path}", ex);
            }

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException($"Required prompt file is empty: {path}");

            // Ignore the final line ending commonly added by text editors.
            return text.TrimEnd('\r', '\n');
        }
    }

    public string Shared { get; }
    public string ChatContext { get; }
    public string Summary { get; }
    public string Reflection { get; }
    public string MemoryMaintenance { get; }
    public string SummaryProjection { get; }
}
