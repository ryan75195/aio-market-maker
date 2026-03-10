namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface IBrandTokenExtractor
{
    Task<IEnumerable<string>> Extract(string searchTerm, CancellationToken ct = default);
}
