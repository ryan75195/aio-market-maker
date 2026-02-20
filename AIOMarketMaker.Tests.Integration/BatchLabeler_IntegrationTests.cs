using AIOMarketMaker.ML.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
public class BatchLabeler_IntegrationTests
{
    [Test]
    [Explicit("Generates a 200MB+ JSONL file from real data")]
    public async Task Should_generate_jsonl_from_real_v8_csv()
    {
        var csvPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");

        if (!File.Exists(csvPath))
        {
            Assert.Ignore($"v8 CSV not found at {Path.GetFullPath(csvPath)}");
        }

        var outputPath = Path.Combine(Path.GetTempPath(), "batch_input_test.jsonl");

        try
        {
            var count = await BatchLabeler.GenerateBatchInput(csvPath, outputPath);

            Assert.That(count, Is.EqualTo(143075));
            Assert.That(File.Exists(outputPath));

            // Verify first few lines are valid JSON
            var lines = File.ReadLines(outputPath).Take(3).ToList();
            foreach (var line in lines)
            {
                Assert.DoesNotThrow(() => System.Text.Json.JsonDocument.Parse(line));
            }

            var fileInfo = new FileInfo(outputPath);
            TestContext.WriteLine($"Generated {count:N0} lines, file size: {fileInfo.Length / 1024 / 1024}MB");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
