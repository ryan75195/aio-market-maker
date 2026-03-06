namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface INgramExtractor
{
    IEnumerable<Ngram> Extract(IEnumerable<string> titles);
    Task<IEnumerable<Ngram>> Deduplicate(IEnumerable<Ngram> ngrams, CancellationToken ct = default);
}
