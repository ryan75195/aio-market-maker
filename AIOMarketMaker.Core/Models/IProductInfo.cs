namespace AIOMarketMaker.Core.Models;

/// <summary>
/// Interface representing minimal product information needed for vector indexing.
/// </summary>
public interface IProductInfo
{
    int Id { get; }
    string? ProductName { get; }
    string? Category { get; }
    string? Brand { get; }
}
