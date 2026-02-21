using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class OnnxVariantClassifier_UnitTests
{
    [Test]
    public void Should_throw_when_model_file_missing()
    {
        var config = new OnnxClassifierConfig(
            ModelPath: "/nonexistent/path/model.onnx",
            VocabPath: "/nonexistent/vocab.json",
            MergesPath: "/nonexistent/merges.txt");

        var ex = Assert.Throws<FileNotFoundException>(() =>
            new OnnxVariantClassifier(config, Mock.Of<ILogger<OnnxVariantClassifier>>()));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("ONNX model not found"));
            Assert.That(ex.Message, Does.Contain("gpu-setup.md"));
        });
    }

    [Test]
    public void Should_tokenize_pair_with_correct_roberta_format()
    {
        var vocabPath = FindModelFile("vocab.json");
        var mergesPath = FindModelFile("merges.txt");

        var (inputIds, attentionMask, _) = OnnxVariantClassifier.TokenizePair(
            vocabPath, mergesPath,
            "Dyson V15 Detect Absolute | Cordless vacuum cleaner with laser dust detection",
            "Dyson V15 Detect Absolute | Brand new cordless vacuum with laser illuminate technology",
            maxLength: 256);

        Assert.Multiple(() =>
        {
            // BOS token
            Assert.That(inputIds[0], Is.EqualTo(0), "First token should be <s> (BOS=0)");
            // EOS separator pair at position 16-17
            Assert.That(inputIds[16], Is.EqualTo(2), "Should have </s> separator");
            Assert.That(inputIds[17], Is.EqualTo(2), "Should have </s></s> double separator");
            // Total non-pad tokens = 35 (verified from Python)
            Assert.That(attentionMask.Take(35).All(m => m == 1), Is.True, "First 35 should be attended");
            Assert.That(attentionMask.Skip(35).All(m => m == 0), Is.True, "Rest should be padding");
            // Padding token
            Assert.That(inputIds[35], Is.EqualTo(1), "Padding should be <pad> (PAD=1)");
            // Array lengths
            Assert.That(inputIds, Has.Length.EqualTo(256));
            Assert.That(attentionMask, Has.Length.EqualTo(256));
        });

        // Exact token match for first 30 tokens (verified against Python)
        long[] pythonRef = [0, 495, 20216, 468, 996, 18129, 42001, 1721, 11931, 1672,
            15702, 16126, 19, 13443, 8402, 12673, 2, 2, 495, 20216,
            468, 996, 18129, 42001, 1721, 7379, 92, 13051, 1672, 15702];
        Assert.That(inputIds.Take(30).ToArray(), Is.EqualTo(pythonRef),
            "First 30 tokens should match Python reference exactly");
    }

    [Test]
    public void Should_truncate_long_inputs_to_max_length()
    {
        var vocabPath = FindModelFile("vocab.json");
        var mergesPath = FindModelFile("merges.txt");

        // Create very long text that would exceed 256 tokens
        var longText = string.Join(" ", Enumerable.Repeat("premium quality professional grade", 50));

        var (inputIds, attentionMask, _) = OnnxVariantClassifier.TokenizePair(
            vocabPath, mergesPath, longText, longText, maxLength: 64);

        Assert.Multiple(() =>
        {
            Assert.That(inputIds, Has.Length.EqualTo(64));
            Assert.That(attentionMask, Has.Length.EqualTo(64));
            Assert.That(attentionMask.All(m => m == 1), Is.True, "All tokens should be attended (no padding after truncation)");
        });
    }

    [Test]
    public void Should_apply_softmax_correctly()
    {
        // Reference from Python ONNX: logits [-2.284080, 3.348258] -> probs [0.003567, 0.996433]
        var probs = OnnxVariantClassifier.Softmax([-2.284080f, 3.348258f]);

        Assert.Multiple(() =>
        {
            Assert.That(probs[0], Is.EqualTo(0.003567f).Within(0.0001f));
            Assert.That(probs[1], Is.EqualTo(0.996433f).Within(0.0001f));
            Assert.That(probs.Sum(), Is.EqualTo(1.0f).Within(0.0001f));
        });
    }

    [Test]
    public void Should_apply_batch_softmax_correctly()
    {
        var batchLogits = new float[,]
        {
            { -2.284080f, 3.348258f },
            { 1.5f, -1.5f }
        };

        var results = OnnxVariantClassifier.BatchSoftmax(batchLogits);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Length.EqualTo(2));
            Assert.That(results[0][0], Is.EqualTo(0.003567f).Within(0.0001f));
            Assert.That(results[0][1], Is.EqualTo(0.996433f).Within(0.0001f));
            Assert.That(results[1][0], Is.EqualTo(0.9526f).Within(0.001f));
            Assert.That(results[1][1], Is.EqualTo(0.0474f).Within(0.001f));
        });
    }

    private static string FindModelFile(string filename)
    {
        // Walk up from test bin directory to find models/variant-classifier/
        var dir = TestContext.CurrentContext.TestDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "models", "variant-classifier", filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir)!;
        }
        Assert.Ignore($"Model file {filename} not found — skipping tokenizer test");
        return null!; // unreachable
    }
}
