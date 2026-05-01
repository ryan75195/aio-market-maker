namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface ICommunityDetector
{
    IEnumerable<Community> Detect(
        IEnumerable<WeightedEdge> edges, int nodeCount, double resolution = 2.0);
}
