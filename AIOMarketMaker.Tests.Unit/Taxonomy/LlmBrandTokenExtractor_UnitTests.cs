using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class LlmBrandTokenExtractor_UnitTests
{
    [Test]
    public async Task Should_extract_brand_tokens_from_search_term()
    {
        var extractor = new FakeBrandTokenExtractor("""{"tokens":["rolex"]}""");

        var tokens = await extractor.Extract("Rolex Submariner");

        Assert.That(tokens, Is.EqualTo(new[] { "rolex" }));
    }

    [Test]
    public async Task Should_extract_multiple_tokens()
    {
        var extractor = new FakeBrandTokenExtractor("""{"tokens":["playstation","ps5"]}""");

        var tokens = (await extractor.Extract("PlayStation 5 Console")).ToList();

        Assert.That(tokens, Has.Exactly(2).Items);
        Assert.That(tokens, Does.Contain("playstation"));
        Assert.That(tokens, Does.Contain("ps5"));
    }

    [Test]
    public async Task Should_return_empty_for_empty_search_term()
    {
        var extractor = new FakeBrandTokenExtractor("""{"tokens":[]}""");

        var tokens = await extractor.Extract("");

        Assert.That(tokens, Is.Empty);
    }

    [Test]
    public async Task Should_lowercase_and_trim_tokens()
    {
        var extractor = new FakeBrandTokenExtractor("""{"tokens":["  ROLEX  ", " Submariner "]}""");

        var tokens = (await extractor.Extract("Rolex Submariner")).ToList();

        Assert.That(tokens, Is.EqualTo(new[] { "rolex", "submariner" }));
    }

    [Test]
    public async Task Should_filter_out_empty_tokens()
    {
        var extractor = new FakeBrandTokenExtractor("""{"tokens":["rolex", "", "  "]}""");

        var tokens = (await extractor.Extract("Rolex Submariner")).ToList();

        Assert.That(tokens, Has.Exactly(1).Items);
        Assert.That(tokens.First(), Is.EqualTo("rolex"));
    }
}

/// <summary>
/// Test double that simulates LLM response parsing without calling OpenAI.
/// Parses the same JSON format as LlmBrandTokenExtractor.
/// </summary>
internal class FakeBrandTokenExtractor : IBrandTokenExtractor
{
    private readonly string _json;

    public FakeBrandTokenExtractor(string json)
    {
        _json = json;
    }

    public Task<IEnumerable<string>> Extract(string searchTerm, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Task.FromResult<IEnumerable<string>>(Array.Empty<string>());
        }

        var response = System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(_json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize");

        var tokens = response.Tokens
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0);

        return Task.FromResult(tokens);
    }

    private record TokenResponse(string[] Tokens);
}
