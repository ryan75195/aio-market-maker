using AIOMarketMaker.Services.Dtos;

namespace AIOMarketMaker.Services;

public interface IProductService
{
    Task<PagedResult<ProductDto>> GetProductsAsync(ProductFilter filter, CancellationToken ct = default);
    Task<List<ProductNameSummary>> GetProductNamesAsync(string? category, CancellationToken ct = default);
    Task<ProductVariants?> GetProductVariantsAsync(string productName, CancellationToken ct = default);
}
