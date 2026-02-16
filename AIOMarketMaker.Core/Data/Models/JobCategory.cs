namespace AIOMarketMaker.Core.Data.Models;

public class JobCategory
{
    public int JobId { get; set; }
    public ScrapeJob Job { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}
