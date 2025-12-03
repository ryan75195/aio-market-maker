using AIOMarketMaker.Services.Dtos;

namespace AIOMarketMaker.Services;

public interface IJobService
{
    Task<List<JobDto>> GetAllJobsAsync(CancellationToken ct = default);
    Task<JobDto> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default);
    Task<JobDto?> UpdateJobAsync(int id, CreateJobRequest request, CancellationToken ct = default);
    Task<(bool found, bool isEnabled)?> ToggleJobAsync(int id, CancellationToken ct = default);
    Task<DeleteJobResult?> DeleteJobAsync(int id, CancellationToken ct = default);
}
