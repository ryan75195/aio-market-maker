using AIOMarketMaker.Services.Dtos;

namespace AIOMarketMaker.Services;

public interface IListingService
{
    Task<PagedResult<ListingDto>> GetListingsAsync(ListingFilter filter, CancellationToken ct = default);
    Task<ListingDetails?> GetListingDetailsAsync(int id, CancellationToken ct = default);
}
