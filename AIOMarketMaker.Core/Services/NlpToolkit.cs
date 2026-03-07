namespace AIOMarketMaker.Core.Services;

public interface INlpToolkit
{
    string Singularize(string word);
}

public class NlpToolkit : INlpToolkit
{
    private static readonly HashSet<string> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "series", "lens", "species", "canvas", "alias", "bonus", "campus",
        "corpus", "ibus", "atlas", "plus", "minus"
    };

    public string Singularize(string word)
    {
        if (word.Length <= 3)
        {
            return word;
        }

        if (Exceptions.Contains(word))
        {
            return word;
        }

        if (word.EndsWith("ies") && word.Length > 4)
        {
            return word[..^3] + "y";
        }

        // shelves→shelf, halves→half, wolves→wolf
        if ((word.EndsWith("lves") || word.EndsWith("aves")) && word.Length > 5)
        {
            return word[..^3] + "f";
        }

        if (word.EndsWith("ses") && word.Length > 4)
        {
            return word[..^1];
        }

        if (word.EndsWith("ss") || word.EndsWith("us"))
        {
            return word;
        }

        if (word.EndsWith("s"))
        {
            return word[..^1];
        }

        return word;
    }
}
