using AIOMarketMaker.Core.Data.Models;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record ModelFirstComparisonConfig(float ConfidenceThreshold = 0.80f, bool EnableGptFallback = true);

public class ModelFirstComparisonService : IListingComparisonService
{
    private readonly IVariantClassifierClient _classifier;
    private readonly IListingComparisonService _gptFallback;
    private readonly float _confidenceThreshold;
    private readonly bool _enableGptFallback;
    private readonly ILogger<ModelFirstComparisonService> _logger;

    public ModelFirstComparisonService(
        IVariantClassifierClient classifier,
        IListingComparisonService gptFallback,
        ModelFirstComparisonConfig config,
        ILogger<ModelFirstComparisonService> logger)
    {
        _classifier = classifier;
        _gptFallback = gptFallback;
        _confidenceThreshold = config.ConfidenceThreshold;
        _enableGptFallback = config.EnableGptFallback;
        _logger = logger;
    }

    public async Task<ComparableVerdict> Compare(Listing a, Listing b, CancellationToken ct = default)
    {
        try
        {
            var results = await _classifier.Classify(new[]
            {
                new ClassifyPairRequest(
                    a.Title ?? "", a.Description ?? "",
                    b.Title ?? "", b.Description ?? "")
            }, ct);

            var result = results[0];

            if (result.Confidence >= _confidenceThreshold || !_enableGptFallback)
            {
                _logger.LogDebug(
                    "Model verdict for ({IdA}, {IdB}): {Verdict} (confidence={Confidence:F3})",
                    a.Id, b.Id, result.IsComparable ? "comparable" : "different", result.Confidence);

                return new ComparableVerdict(
                    result.IsComparable,
                    $"Model: confidence={result.Confidence:F3}");
            }

            _logger.LogDebug(
                "Model uncertain for ({IdA}, {IdB}), confidence={Confidence:F3} — falling back to GPT",
                a.Id, b.Id, result.Confidence);
        }
        catch (Exception ex)
        {
            if (!_enableGptFallback)
            {
                throw;
            }

            _logger.LogWarning(ex,
                "Model service unavailable, falling back to GPT for ({IdA}, {IdB})",
                a.Id, b.Id);
        }

        return await _gptFallback.Compare(a, b, ct);
    }
}
