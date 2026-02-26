namespace AIOMarketMaker.Console.Tasks;

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

    public static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }
        return text[..maxChars];
    }
}
