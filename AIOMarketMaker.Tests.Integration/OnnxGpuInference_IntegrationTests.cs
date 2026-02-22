using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Moq;
using NUnit.Framework;
using System.Diagnostics;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires ONNX model files and CUDA Toolkit 12.x")]
public class OnnxGpuInference_IntegrationTests
{
    private const string ModelDir = "E:/Dev/ml-training/variant-classifier/v8/onnx";

    [Test]
    public void Should_load_cuda_execution_provider()
    {
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        // This will throw if CUDA provider DLLs are missing (cublasLt64_12.dll, cudnn, etc.)
        Assert.DoesNotThrow(() => sessionOptions.AppendExecutionProvider_CUDA(0),
            "CUDA provider should load. Install CUDA Toolkit 12.x and cuDNN 9.x. See docs/gpu-setup.md");

        var modelPath = Path.Combine(ModelDir, "model.onnx");
        using var session = new InferenceSession(modelPath, sessionOptions);

        // Verify CUDA is actually in the provider list
        TestContext.WriteLine($"Session providers: {string.Join(", ", session.InputMetadata.Keys)}");
        TestContext.WriteLine("CUDA provider loaded successfully");
    }

    [Test]
    public void Should_use_gpu_in_onnx_variant_classifier()
    {
        var logger = new Mock<ILogger<VariantModelRunner>>();
        var config = new OnnxClassifierConfig(
            ModelPath: Path.Combine(ModelDir, "model.onnx"),
            VocabPath: Path.Combine(ModelDir, "vocab.json"),
            MergesPath: Path.Combine(ModelDir, "merges.txt"));

        // Capture log messages to verify CUDA was used
        var logMessages = new List<string>();
        logger.Setup(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback((LogLevel level, EventId id, object state, Exception? ex, Delegate formatter) =>
            {
                logMessages.Add(formatter.DynamicInvoke(state, ex)?.ToString() ?? "");
            });

        using var classifier = new VariantModelRunner(config, logger.Object);

        var usedCuda = logMessages.Any(m => m.Contains("CUDA GPU"));
        var fellBackToCpu = logMessages.Any(m => m.Contains("CUDA not available"));

        foreach (var msg in logMessages)
        {
            TestContext.WriteLine($"  LOG: {msg}");
        }

        Assert.That(usedCuda, Is.True,
            "Expected 'CUDA GPU' in logs. Got CPU fallback instead. " +
            "Install CUDA Toolkit 12.x and cuDNN 9.x. See docs/gpu-setup.md");
        Assert.That(fellBackToCpu, Is.False,
            "CUDA fell back to CPU. Check CUDA Toolkit installation.");
    }

    [Test]
    public async Task Should_achieve_gpu_level_latency()
    {
        var config = new OnnxClassifierConfig(
            ModelPath: Path.Combine(ModelDir, "model.onnx"),
            VocabPath: Path.Combine(ModelDir, "vocab.json"),
            MergesPath: Path.Combine(ModelDir, "merges.txt"));

        using var classifier = new VariantModelRunner(config, Mock.Of<ILogger<VariantModelRunner>>());

        var pair = new ClassifyPairRequest(
            "Sony PlayStation 5 Slim Disc Edition Console",
            "PS5 Slim with disc drive, includes DualSense controller",
            "PS5 Slim Disc Edition - Sony PlayStation 5 Console",
            "Sony PlayStation 5 Slim disc edition, brand new sealed");

        // Warm up — first CUDA inference compiles kernels (~60-80s)
        await classifier.Classify([pair]);

        // Measure steady-state latency
        var sw = Stopwatch.StartNew();
        const int iterations = 20;
        for (var i = 0; i < iterations; i++)
        {
            await classifier.Classify([pair]);
        }
        sw.Stop();

        var avgMs = sw.ElapsedMilliseconds / (double)iterations;
        TestContext.WriteLine($"Average latency: {avgMs:F1} ms/pair over {iterations} iterations");

        // GPU: ~13ms, CPU: ~700ms. Use 100ms as GPU threshold.
        Assert.That(avgMs, Is.LessThan(100),
            $"Average latency {avgMs:F1}ms exceeds GPU threshold (100ms). " +
            "Likely running on CPU. Install CUDA Toolkit 12.x. See docs/gpu-setup.md");
    }

    [Test]
    public async Task Should_achieve_gpu_batch_throughput()
    {
        var config = new OnnxClassifierConfig(
            ModelPath: Path.Combine(ModelDir, "model.onnx"),
            VocabPath: Path.Combine(ModelDir, "vocab.json"),
            MergesPath: Path.Combine(ModelDir, "merges.txt"));

        using var classifier = new VariantModelRunner(config, Mock.Of<ILogger<VariantModelRunner>>());

        // Build a batch of 128 pairs (same size as ETL batches)
        var pairs = Enumerable.Range(0, 128).Select(i => new ClassifyPairRequest(
            $"Product A variant {i}",
            $"Description for product A variant {i}",
            $"Product B variant {i}",
            $"Description for product B variant {i}")
        ).ToList();

        // Warm up
        await classifier.Classify(pairs.Take(1));

        // Measure batch inference
        var sw = Stopwatch.StartNew();
        var results = await classifier.Classify(pairs);
        sw.Stop();

        var totalMs = sw.ElapsedMilliseconds;
        var perPairMs = totalMs / (double)pairs.Count;
        TestContext.WriteLine($"Batch of {pairs.Count}: {totalMs}ms total, {perPairMs:F1}ms/pair");

        Assert.That(results.Count, Is.EqualTo(128), "Should return results for all pairs");

        // GPU batch: ~2-3s for 128 pairs. CPU batch: ~90s. Use 10s as GPU threshold.
        Assert.That(totalMs, Is.LessThan(10000),
            $"Batch inference took {totalMs}ms for 128 pairs. " +
            "GPU should complete in <10s. Likely running on CPU.");
    }
}
