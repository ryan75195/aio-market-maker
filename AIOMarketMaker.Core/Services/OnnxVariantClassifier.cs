using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AIOMarketMaker.Core.Services;

public interface IVariantClassifierClient
{
    Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default);

    Task<bool> IsHealthy(CancellationToken ct = default);
}

public record ClassifyPairRequest(
    string TitleA,
    string DescriptionA,
    string TitleB,
    string DescriptionB);

public record PairResult(bool IsComparable, float Confidence);

public record OnnxClassifierConfig(
    string ModelPath,
    string VocabPath,
    string MergesPath,
    int MaxLength = 256,
    int BatchSize = 128);

public class OnnxVariantClassifier : IVariantClassifierClient, IDisposable
{
    private const long BosId = 0;  // <s>
    private const long EosId = 2;  // </s>
    private const long PadId = 1;  // <pad>

    private readonly InferenceSession _session;
    private readonly CodeGenTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly ILogger<OnnxVariantClassifier> _logger;
    private readonly bool _isHealthy;

    public OnnxVariantClassifier(OnnxClassifierConfig config, ILogger<OnnxVariantClassifier> logger)
    {
        _maxLength = config.MaxLength;
        _logger = logger;

        if (!File.Exists(config.ModelPath))
        {
            throw new FileNotFoundException(
                $"ONNX model not found at '{config.ModelPath}'. See docs/gpu-setup.md for setup instructions.",
                config.ModelPath);
        }

        // Load tokenizer
        using var vocabStream = File.OpenRead(config.VocabPath);
        using var mergesStream = File.OpenRead(config.MergesPath);
        _tokenizer = CodeGenTokenizer.Create(vocabStream, mergesStream,
            addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);

        // Load ONNX model — try CUDA first, fall back to CPU
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        try
        {
            sessionOptions.AppendExecutionProvider_CUDA(0);
            _logger.LogInformation("ONNX variant classifier using CUDA GPU");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("CUDA not available ({Message}), falling back to CPU. " +
                "GPU inference is ~60x faster. See docs/gpu-setup.md", ex.Message);
            sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;
        }

        _session = new InferenceSession(config.ModelPath, sessionOptions);
        _isHealthy = true;
        _logger.LogInformation("ONNX variant classifier loaded from {ModelPath}", config.ModelPath);
    }

    public Task<IReadOnlyList<PairResult>> Classify(
        IEnumerable<ClassifyPairRequest> pairs,
        CancellationToken ct = default)
    {
        var pairList = pairs.ToList();
        if (pairList.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PairResult>>(Array.Empty<PairResult>());
        }

        ct.ThrowIfCancellationRequested();

        // Tokenize all pairs
        var allInputIds = new long[pairList.Count * _maxLength];
        var allAttentionMask = new long[pairList.Count * _maxLength];

        for (var i = 0; i < pairList.Count; i++)
        {
            var pair = pairList[i];
            var textA = $"{pair.TitleA} | {pair.DescriptionA}";
            var textB = $"{pair.TitleB} | {pair.DescriptionB}";
            var (inputIds, attentionMask) = TokenizePairInternal(textA, textB);

            Array.Copy(inputIds, 0, allInputIds, i * _maxLength, _maxLength);
            Array.Copy(attentionMask, 0, allAttentionMask, i * _maxLength, _maxLength);
        }

        // Build batched tensors [N, maxLength]
        var inputTensor = new DenseTensor<long>(allInputIds, [pairList.Count, _maxLength]);
        var maskTensor = new DenseTensor<long>(allAttentionMask, [pairList.Count, _maxLength]);
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
        };

        // Single inference call for entire batch
        using var output = _session.Run(inputs);
        var logitsTensor = output.First().AsTensor<float>();

        // Parse results
        var results = new List<PairResult>(pairList.Count);
        for (var i = 0; i < pairList.Count; i++)
        {
            var logits = new float[] { logitsTensor[i, 0], logitsTensor[i, 1] };
            var probs = Softmax(logits);
            var isComparable = probs[1] > probs[0];
            var confidence = probs.Max();
            results.Add(new PairResult(isComparable, confidence));
        }

        return Task.FromResult<IReadOnlyList<PairResult>>(results);
    }

    public Task<bool> IsHealthy(CancellationToken ct = default)
    {
        return Task.FromResult(_isHealthy);
    }

    /// <summary>
    /// Static tokenization method exposed for unit testing without requiring a model file.
    /// </summary>
    public static (long[] InputIds, long[] AttentionMask) TokenizePair(
        string vocabPath, string mergesPath, string textA, string textB, int maxLength)
    {
        using var vocabStream = File.OpenRead(vocabPath);
        using var mergesStream = File.OpenRead(mergesPath);
        var tokenizer = CodeGenTokenizer.Create(vocabStream, mergesStream,
            addPrefixSpace: false, addBeginOfSentence: false, addEndOfSentence: false);

        return TokenizePairCore(tokenizer, textA, textB, maxLength);
    }

    public static float[] Softmax(float[] logits)
    {
        var max = logits.Max();
        var exps = logits.Select(l => MathF.Exp(l - max)).ToArray();
        var sum = exps.Sum();
        return exps.Select(e => e / sum).ToArray();
    }

    public static float[][] BatchSoftmax(float[,] batchLogits)
    {
        var batchSize = batchLogits.GetLength(0);
        var numClasses = batchLogits.GetLength(1);
        var results = new float[batchSize][];

        for (var i = 0; i < batchSize; i++)
        {
            var logits = new float[numClasses];
            for (var j = 0; j < numClasses; j++)
            {
                logits[j] = batchLogits[i, j];
            }
            results[i] = Softmax(logits);
        }

        return results;
    }

    private (long[] InputIds, long[] AttentionMask) TokenizePairInternal(string textA, string textB)
    {
        return TokenizePairCore(_tokenizer, textA, textB, _maxLength);
    }

    private static (long[] InputIds, long[] AttentionMask) TokenizePairCore(
        CodeGenTokenizer tokenizer, string textA, string textB, int maxLength)
    {
        var idsA = tokenizer.EncodeToIds(textA);
        var idsB = tokenizer.EncodeToIds(textB);

        // RoBERTa sentence pair format: <s> tokens_a </s></s> tokens_b </s>
        var combined = new List<long>(maxLength);
        combined.Add(BosId);
        foreach (var id in idsA)
        {
            combined.Add(id);
        }
        combined.Add(EosId);
        combined.Add(EosId);
        foreach (var id in idsB)
        {
            combined.Add(id);
        }
        combined.Add(EosId);

        // Truncate if needed
        if (combined.Count > maxLength)
        {
            combined.RemoveRange(maxLength, combined.Count - maxLength);
        }

        var realLength = combined.Count;

        // Pad to maxLength
        while (combined.Count < maxLength)
        {
            combined.Add(PadId);
        }

        // Build attention mask
        var mask = new long[maxLength];
        for (var i = 0; i < realLength; i++)
        {
            mask[i] = 1;
        }

        return (combined.ToArray(), mask);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
