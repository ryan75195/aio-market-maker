using AIOMarketMaker.Services.Dtos;

namespace AIOMarketMaker.Services;

public interface IMetricsService
{
    Task<DashboardMetrics> GetDashboardMetricsAsync(CancellationToken ct = default);
}
