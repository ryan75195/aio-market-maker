using AIOMarketMaker.ML.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
public class BatchLabeler_IntegrationTests
{
    [Test]
    [Explicit("Generates 200MB+ JSONL files from real data")]
    public async Task Should_generate_jsonl_from_real_v8_csv()
    {
        var csvPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.ML", "Training", "data", "labeled_pairs_v8.csv");

        if (!File.Exists(csvPath))
        {
            Assert.Ignore($"v8 CSV not found at {Path.GetFullPath(csvPath)}");
        }

        var outputDir = Path.Combine(Path.GetTempPath(), $"batch_integ_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var (files, totalPairs) = await BatchLabeler.GenerateBatchInput(csvPath, outputDir);
            var fileList = files.ToList();

            Assert.That(totalPairs, Is.EqualTo(143075));
            Assert.That(fileList, Has.Count.EqualTo(3)); // 143K / 50K = 3 chunks

            // Verify first lines of each chunk are valid JSON
            foreach (var file in fileList)
            {
                var firstLine = File.ReadLines(file).First();
                Assert.DoesNotThrow(() => System.Text.Json.JsonDocument.Parse(firstLine));
                var fileInfo = new FileInfo(file);
                TestContext.WriteLine($"{Path.GetFileName(file)}: {fileInfo.Length / 1024 / 1024}MB");
            }

            TestContext.WriteLine($"Generated {totalPairs:N0} pairs across {fileList.Count} files");
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
