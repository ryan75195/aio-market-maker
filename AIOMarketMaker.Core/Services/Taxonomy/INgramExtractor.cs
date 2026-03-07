namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface INgramExtractor
{
    IEnumerable<RawNgram> Extract(IEnumerable<string> titles);
    Task<IEnumerable<Ngram>> MergeSynonyms(IEnumerable<RawNgram> rawNgrams, CancellationToken ct = default);
}
