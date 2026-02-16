namespace AIOMarketMaker.Core.Data.Models;

public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<JobCategory> JobCategories { get; set; } = new List<JobCategory>();
}
