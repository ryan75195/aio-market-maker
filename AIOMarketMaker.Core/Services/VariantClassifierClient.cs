using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record VariantClassifierConfig(string BaseUrl = "http://localhost:8010");

public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB);

public record PairResult(bool IsComparable, float Confidence, bool NeedsFallback);

public record ClassifyResponse(IReadOnlyList<PairResult> Results);

public interface IVariantClassifierClient
{
    Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default);

    Task<bool> IsHealthy(CancellationToken ct = default);
}

public class VariantClassifierClient : IVariantClassifierClient
{
    private readonly HttpClient _http;
    private readonly ILogger<VariantClassifierClient> _logger;

    public VariantClassifierClient(HttpClient http, ILogger<VariantClassifierClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var payload = new
        {
            pairs = pairs.Select(p => new
            {
                title_a = p.TitleA,
                description_a = p.DescriptionA,
                title_b = p.TitleB,
                description_b = p.DescriptionB
            })
        };

        var response = await _http.PostAsJsonAsync("classify", payload, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClassifyResponse>(ct);
        return result?.Results ?? Array.Empty<PairResult>();
    }

    public async Task<bool> IsHealthy(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
