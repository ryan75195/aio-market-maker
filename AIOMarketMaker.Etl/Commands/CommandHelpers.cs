namespace AIOMarketMaker.Etl.Commands;

public static class CommandHelpers
{
    public static int? GetIntArg(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value))
        {
            return value;
        }
        return null;
    }

    public static string? GetStringArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    public static string BuildEmbeddingText(string? title, string? description)
    {
        return string.Join(" ", new[] { title, description }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }
        return text[..maxChars];
    }
}
