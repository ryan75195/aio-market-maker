namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface IMutualExclusivityAnalyzer
{
    IEnumerable<MatchSet> ComputeMatchSets(IEnumerable<string> titles, IEnumerable<Ngram> ngrams);
    IEnumerable<MutuallyExclusivePair> FindExclusivePairs(
        IEnumerable<MatchSet> matchSets, double threshold = 0.05);
}
