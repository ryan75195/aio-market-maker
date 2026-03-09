namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface INgramExtractor
{
    IEnumerable<RawNgram> Extract(IEnumerable<string> titles, string? searchTerm = null);
    Task<IEnumerable<Ngram>> MergeSynonyms(IEnumerable<RawNgram> rawNgrams, CancellationToken ct = default);
    Task<IEnumerable<Ngram>> SubsumeByTokenOverlap(IEnumerable<Ngram> ngrams, CancellationToken ct = default);
}
