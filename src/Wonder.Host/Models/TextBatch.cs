namespace Wonder.Host;

public sealed record TextFragment<T>(T Source, string Text, int Offset, bool Truncated);

public static class TextBatch
{
    public static string Fingerprint(string text) => Wonder.Core.Json.Hash(Wonder.Core.Json.Write(text));
    public static List<TextFragment<T>> Select<T>(IReadOnlyList<T> source, Func<T, string> text, int offset,
        Func<IReadOnlyList<TextFragment<T>>, bool> fits, int maxCharacters = int.MaxValue)
    {
        var batch = new List<TextFragment<T>>();
        foreach (var item in source)
        {
            var original = text(item);
            var remaining = original[offset..];
            var length = Math.Min(remaining.Length, maxCharacters);
            if (length > 0 && length < remaining.Length && char.IsHighSurrogate(remaining[length - 1])) length--;
            var fragment = new TextFragment<T>(item, remaining[..length], offset, offset > 0 || length < remaining.Length);
            batch.Add(fragment);
            if (!fits(batch))
            {
                batch.RemoveAt(batch.Count - 1);
                if (batch.Count > 0) break;
                length = ModelContextBudget.PrefixLength(remaining[..length], value =>
                    fits([new(item, value, offset, true)]));
                if (length == 0) throw new ContextLimitException("Instructions and required metadata leave no room for source text.");
                batch.Add(new(item, remaining[..length], offset, true));
            }
            if (offset + length < original.Length) break;
            maxCharacters -= length;
            if (maxCharacters <= 0) break;
            offset = 0;
        }
        return batch;
    }
}
