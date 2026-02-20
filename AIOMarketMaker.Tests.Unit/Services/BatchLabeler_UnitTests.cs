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
}
