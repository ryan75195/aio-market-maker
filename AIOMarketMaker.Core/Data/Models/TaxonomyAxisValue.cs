namespace AIOMarketMaker.Core.Data.Models;

public class TaxonomyAxisValue
{
    public int Id { get; set; }
    public int TaxonomyAxisId { get; set; }
    public required string Label { get; set; }
    public string? NgramsJson { get; set; }
    public int SortOrder { get; set; }

    public TaxonomyAxis TaxonomyAxis { get; set; } = null!;
}
