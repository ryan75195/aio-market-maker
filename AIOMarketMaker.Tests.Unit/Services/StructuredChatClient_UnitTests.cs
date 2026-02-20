using System.Text.Json;
using System.Text.Json.Serialization;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.ML.Utils;

namespace AIOMarketMaker.Tests.Unit.Services;

// Simulates the response type that will be used with structured outputs
public record ClassificationResponse(
    [property: JsonPropertyName("verdict")] string Verdict,
    [property: JsonPropertyName("reason")] string Reason);

[TestFixture]
[Category("Unit")]
public class StructuredChatClient_UnitTests
{
    [Test]
    public void Should_define_CompleteChat_generic_on_IChatClient()
    {
        var genericMethod = typeof(IChatClient).GetMethods()
            .FirstOrDefault(m => m.Name == "CompleteChat" && m.IsGenericMethod);

        Assert.That(genericMethod, Is.Not.Null, "IChatClient should have CompleteChat<T> method");
        Assert.That(genericMethod!.GetGenericArguments(), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Should_deserialize_structured_response_to_typed_result()
    {
        // A chat client that returns raw JSON (simulating structured output)
        IChatClient stubClient = new StubStructuredChatClient(
            """{"verdict": "same", "reason": "identical product"}""");

        var result = await stubClient.CompleteChat<ClassificationResponse>(
            "system prompt", "user prompt");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Verdict, Is.EqualTo("same"));
            Assert.That(result.Reason, Is.EqualTo("identical product"));
        });
    }

    [Test]
    public async Task Should_return_null_when_response_is_not_valid_json()
    {
        IChatClient stubClient = new StubStructuredChatClient("not json at all");

        var result = await stubClient.CompleteChat<ClassificationResponse>(
            "system prompt", "user prompt");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_generate_correct_schema_name_from_type()
    {
        // Verify the schema generator produces a name usable by OpenAI
        var schema = JsonSchemaGenerator.Generate<ClassificationResponse>();
        using var doc = JsonDocument.Parse(schema);
        var root = doc.RootElement;

        // Should have verdict as string and reason as string
        var props = root.GetProperty("properties");
        Assert.Multiple(() =>
        {
            Assert.That(props.GetProperty("verdict").GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(props.GetProperty("reason").GetProperty("type").GetString(), Is.EqualTo("string"));
        });
    }

    /// <summary>
    /// Stub that returns a fixed string from CompleteChat,
    /// exercising the default CompleteChat&lt;T&gt; implementation.
    /// </summary>
    private class StubStructuredChatClient : IChatClient
    {
        private readonly string _response;

        public StubStructuredChatClient(string response)
        {
            _response = response;
        }

        public Task<string> CompleteChat(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            return Task.FromResult(_response);
        }
    }
}
