using System.Text.Json;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class BatchLabeler_UnitTests
{
    [Test]
    public void Should_generate_valid_jsonl_line_for_pair()
    {
        var pair = new ClassifyPairRequest(
            "iPhone 15 Pro Max 256GB",
            "Brand new sealed iPhone 15 Pro Max",
            "Apple iPhone 15 Pro Max 256GB Black Titanium",
            "Apple iPhone 15 Pro Max 256GB in Black Titanium, factory unlocked");

        var line = BatchLabeler.BuildBatchRequestLine("pair-42", pair);
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("custom_id").GetString(), Is.EqualTo("pair-42"));
            Assert.That(root.GetProperty("method").GetString(), Is.EqualTo("POST"));
            Assert.That(root.GetProperty("url").GetString(), Is.EqualTo("/v1/chat/completions"));

            var body = root.GetProperty("body");
            Assert.That(body.GetProperty("model").GetString(), Is.EqualTo("gpt-5-mini"));

            var messages = body.GetProperty("messages");
            Assert.That(messages.GetArrayLength(), Is.EqualTo(2));
            Assert.That(messages[0].GetProperty("role").GetString(), Is.EqualTo("system"));
            Assert.That(messages[1].GetProperty("role").GetString(), Is.EqualTo("user"));
            Assert.That(messages[1].GetProperty("content").GetString(), Does.Contain("iPhone 15 Pro Max 256GB"));

            var responseFormat = body.GetProperty("response_format");
            Assert.That(responseFormat.GetProperty("type").GetString(), Is.EqualTo("json_schema"));
        });
    }

    [Test]
    public void Should_parse_batch_output_line()
    {
        var outputLine = """
            {"id":"batch_req_abc","custom_id":"pair-7","response":{"status_code":200,"body":{"choices":[{"message":{"content":"{\"reason\":\"Same product and condition\",\"verdict\":\"same\"}"}}]}}}
            """;

        var result = BatchLabeler.ParseBatchOutputLine(outputLine);

        Assert.Multiple(() =>
        {
            Assert.That(result.CustomId, Is.EqualTo("pair-7"));
            Assert.That(result.Index, Is.EqualTo(7));
            Assert.That(result.Verdict, Is.EqualTo("same"));
            Assert.That(result.Reason, Is.EqualTo("Same product and condition"));
            Assert.That(result.Error, Is.Null);
        });
    }

    [Test]
    public void Should_handle_failed_batch_output_line()
    {
        var outputLine = """
            {"id":"batch_req_abc","custom_id":"pair-3","response":{"status_code":429,"body":{"error":{"message":"Rate limit exceeded"}}}}
            """;

        var result = BatchLabeler.ParseBatchOutputLine(outputLine);

        Assert.Multiple(() =>
        {
            Assert.That(result.CustomId, Is.EqualTo("pair-3"));
            Assert.That(result.Index, Is.EqualTo(3));
            Assert.That(result.Verdict, Is.Null);
            Assert.That(result.Error, Does.Contain("Rate limit"));
        });
    }

    [Test]
    public async Task Should_generate_jsonl_file_from_csv_rows()
    {
        var csvContent = """
            anchor_id,neighbor_id,job_id,product_name,anchor_title,neighbor_title,anchor_desc,neighbor_desc,label,confidence,reasoning,source
            111,222,1,PS5 Console,Sony PS5 Digital,PlayStation 5 Digital Edition,Brand new PS5,New PS5 console,1,high,Both PS5 digital,v5_original
            333,444,2,iPhone 15,iPhone 15 Pro Max,Apple iPhone 15 Pro Max,Good condition,Excellent condition,0,high,Different condition,v7_mined
            """;

        var csvPath = Path.GetTempFileName();
        var outputDir = Path.Combine(Path.GetTempPath(), $"batch_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(csvPath, csvContent);

        try
        {
            var (files, totalPairs) = await BatchLabeler.GenerateBatchInput(csvPath, outputDir);
            var fileList = files.ToList();

            Assert.That(totalPairs, Is.EqualTo(2));
            Assert.That(fileList, Has.Count.EqualTo(1));

            var lines = await File.ReadAllLinesAsync(fileList[0]);
            Assert.That(lines, Has.Length.EqualTo(2));

            // Verify custom_ids are sequential
            var doc0 = JsonDocument.Parse(lines[0]);
            var doc1 = JsonDocument.Parse(lines[1]);
            Assert.That(doc0.RootElement.GetProperty("custom_id").GetString(), Is.EqualTo("pair-0"));
            Assert.That(doc1.RootElement.GetProperty("custom_id").GetString(), Is.EqualTo("pair-1"));
        }
        finally
        {
            File.Delete(csvPath);
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
