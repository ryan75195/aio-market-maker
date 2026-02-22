using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.ML.Services;

public class VariantClassifier : IVariantClassifierClient
{
    private readonly IVariantModelRunner _modelRunner;
    private readonly EnsembleConfig? _ensemble;
    private readonly ILogger<VariantClassifier> _logger;

    public VariantClassifier(
        IVariantModelRunner modelRunner,
        EnsembleConfig? ensemble,
        ILogger<VariantClassifier> logger)
    {
        _modelRunner = modelRunner;
        _ensemble = ensemble;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var pairList = pairs.ToList();
        if (pairList.Count == 0)
        {
            return Array.Empty<PairResult>();
        }

        var rawResults = await _modelRunner.Classify(pairList, ct);
        var calibrated = new List<PairResult>(pairList.Count);

        for (var i = 0; i < pairList.Count; i++)
        {
            var raw = rawResults[i];
            var request = pairList[i];

            if (_ensemble is not null && request.SimilarityScore.HasValue && raw.LogitDiff.HasValue)
            {
                var score = _ensemble.LogitWeight * raw.LogitDiff.Value
                          + _ensemble.SimilarityWeight * request.SimilarityScore.Value
                          + _ensemble.Intercept;
                var confidence = Sigmoid(score);
                var isComparable = confidence > 0.5f;
                calibrated.Add(new PairResult(isComparable, confidence, raw.Reason, raw.LogitDiff));
            }
            else
            {
                calibrated.Add(raw);
            }
        }

        return calibrated;
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return _modelRunner.IsHealthy(ct);
    }

    private static float Sigmoid(float x)
    {
        return 1.0f / (1.0f + MathF.Exp(-x));
    }
}
