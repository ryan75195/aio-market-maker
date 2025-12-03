using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Core.Services.VectorSearch;
using Microsoft.Extensions.Logging;
using Moq;
using Pinecone;
using Pinecone.Rest;

namespace AIOMarketMaker.Tests.Integration;

/// <summary>
/// Integration tests for Pinecone vector database connectivity.
/// These tests require valid Pinecone credentials configured in environment variables or local.settings.json.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Requires Pinecone API key and index")]
public class PineconeIntegrationTests
{
    private PineconeSettings _settings = null!;
    private ILogger<PineconeService> _logger = null!;

    [SetUp]
    public void Setup()
    {
        // Read settings from environment or hardcode for testing
        var apiKey = Environment.GetEnvironmentVariable("PINECONE_API_KEY")
            ?? "REDACTED_PINECONE_KEY";
        var indexName = Environment.GetEnvironmentVariable("PINECONE_INDEX_NAME")
            ?? "arbitrage-products";

        _settings = new PineconeSettings
        {
            ApiKey = apiKey,
            IndexName = indexName,
            TopK = 5,
            SimilarityThreshold = 0.8f
        };

        _logger = Mock.Of<ILogger<PineconeService>>();
    }

    [Test]
    public async Task CanConnectToPineconeIndex()
    {
        // Arrange
        var client = new PineconeClient(_settings.ApiKey);

        // Act & Assert - should not throw
        var index = await client.GetIndex<RestTransport>(_settings.IndexName);
        Assert.That(index, Is.Not.Null);

        Console.WriteLine($"Successfully connected to Pinecone index: {_settings.IndexName}");
    }

    [Test]
    public async Task CanUpsertAndQueryVector()
    {
        // Arrange
        var client = new PineconeClient(_settings.ApiKey);
        var index = await client.GetIndex<RestTransport>(_settings.IndexName);

        var testId = $"test-{Guid.NewGuid():N}";
        var testVector = GenerateRandomVector(1536); // text-embedding-3-small dimensions

        try
        {
            // Act - Upsert
            var vectors = new[]
            {
                new Vector
                {
                    Id = testId,
                    Values = testVector,
                    Metadata = new MetadataMap
                    {
                        ["productName"] = "Test Product",
                        ["category"] = "test",
                        ["brand"] = "TestBrand"
                    }
                }
            };

            await index.Upsert(vectors);
            Console.WriteLine($"Successfully upserted test vector: {testId}");

            // Wait a moment for indexing
            await Task.Delay(1000);

            // Act - Query
            var results = await index.Query(testVector, topK: 1, includeMetadata: true);

            // Assert
            Assert.That(results, Is.Not.Null);
            Assert.That(results.Length, Is.GreaterThan(0), "Query should return at least one result");
            Assert.That(results[0].Id, Is.EqualTo(testId), "Should find the test vector we just upserted");
            Assert.That(results[0].Score, Is.GreaterThan(0.99f), "Should be nearly identical match");

            Console.WriteLine($"Query successful. Score: {results[0].Score}, Metadata: productName={results[0].Metadata?["productName"]}");
        }
        finally
        {
            // Cleanup - delete test vector
            try
            {
                await index.Delete([testId]);
                Console.WriteLine($"Cleaned up test vector: {testId}");
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Test]
    public async Task PineconeServiceCanSearchSimilar()
    {
        // Arrange
        var service = new PineconeService(_settings, _logger);
        var testVector = GenerateRandomVector(1536);

        // Act - should not throw, even if index is empty
        var results = await service.SearchSimilarAsync(testVector, topK: 5);

        // Assert
        Assert.That(results, Is.Not.Null);
        Console.WriteLine($"SearchSimilarAsync returned {results.Count} results");
    }

    private static float[] GenerateRandomVector(int dimensions)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var vector = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1); // -1 to 1
        }

        // Normalize to unit vector
        var magnitude = (float)Math.Sqrt(vector.Sum(v => v * v));
        for (int i = 0; i < dimensions; i++)
        {
            vector[i] /= magnitude;
        }

        return vector;
    }
}
