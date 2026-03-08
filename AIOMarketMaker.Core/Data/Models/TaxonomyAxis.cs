namespace AIOMarketMaker.Core.Data.Models;

public class TaxonomyAxis
{
    public int Id { get; set; }
    public int TaxonomyRunId { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }

    public TaxonomyRun TaxonomyRun { get; set; } = null!;
    public ICollection<TaxonomyAxisValue> Values { get; set; } = new List<TaxonomyAxisValue>();
}
