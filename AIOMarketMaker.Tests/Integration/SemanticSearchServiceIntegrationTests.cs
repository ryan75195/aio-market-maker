using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires local USearch index and OpenAI API key — run manually")]
public class SemanticSearchServiceIntegrationTests
{
    private ISemanticSearchService _service = null!;
    private IEmbeddingService _embeddingService = null!;
    private IVectorIndex _vectorIndex = null!;
    private string _tempDir = null!;

    [SetUp]
    public void Setup()
    {
        var configPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "AIOMarketMaker.Etl", "local.settings.json");

        if (!File.Exists(configPath))
        {
            Assert.Ignore($"local.settings.json not found at {Path.GetFullPath(configPath)}");
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false)
            .Build();

        var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey");

        if (string.IsNullOrEmpty(openAiKey))
        {
            Assert.Ignore("OpenAi:ApiKey not found in local.settings.json");
        }

        var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-large";
        var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 3072);
        var embeddingConfig = new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions);
        var embeddingLogger = new Mock<ILogger<EmbeddingService>>();
        _embeddingService = new EmbeddingService(embeddingConfig, embeddingLogger.Object);

        _tempDir = Path.Combine(Path.GetTempPath(), $"usearch-integ-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var vectorConfig = new VectorIndexConfig(
            IndexPath: Path.Combine(_tempDir, "test.usearch"),
            IdMapPath: Path.Combine(_tempDir, "test-idmap.json"),
            TopK: 10,
            SimilarityThreshold: 0.0f,
            Dimensions: embeddingDimensions);

        _vectorIndex = new USearchVectorIndex(vectorConfig);
        var searchLogger = new Mock<ILogger<SemanticSearchService>>();
        _service = new SemanticSearchService(vectorConfig, _vectorIndex, _embeddingService, searchLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        if (_vectorIndex is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Test]
    public async Task Should_return_zero_counts_when_indexing_empty_list()
    {
        var result = await _service.IndexListings(Array.Empty<Listing>());

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(0));
            Assert.That(result.Errors, Is.Empty);
        });
    }

    [Test]
    public void Should_throw_argument_exception_when_searching_with_empty_query()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _service.Search(""));
    }

    [Test]
    public async Task Should_succeed_when_deleting_empty_list()
    {
        await _service.Delete(Array.Empty<string>());
        Assert.Pass();
    }

    [Test]
    public async Task Should_find_indexed_listing_when_searching()
    {
        var testListingId = $"test-{Guid.NewGuid():N}";
        var listing = new Listing
        {
            ListingId = testListingId,
            Title = "PlayStation 5 Disc Edition Console Bundle",
            Description = "Brand new PS5 disc edition with extra controller"
        };

        var indexResult = await _service.IndexListings(new[] { listing });
        Assert.That(indexResult.UpsertedCount, Is.EqualTo(1));

        var searchResult = await _service.Search("PS5 console disc version");
        Assert.That(searchResult.Hits.Any(h => h.ListingId == testListingId), Is.True,
            "Search should find the indexed listing");
    }

    [Test]
    public async Task Should_find_similar_listings_excluding_self()
    {
        var testIds = new[]
        {
            $"test-ps5-{Guid.NewGuid():N}",
            $"test-ps5-similar-{Guid.NewGuid():N}"
        };

        var listings = new[]
        {
            new Listing
            {
                ListingId = testIds[0],
                Title = "PlayStation 5 Console Disc Edition",
                Description = "Brand new sealed PS5"
            },
            new Listing
            {
                ListingId = testIds[1],
                Title = "PS5 Disc Version Gaming Console",
                Description = "New PlayStation 5 disc edition"
            }
        };

        var indexResult = await _service.IndexListings(listings);
        Assert.That(indexResult.UpsertedCount, Is.EqualTo(2));

        var similarResult = await _service.FindSimilar(testIds[0]);
        Assert.That(similarResult.Hits.Any(h => h.ListingId == testIds[1]), Is.True,
            "Should find similar listing");
        Assert.That(similarResult.Hits.All(h => h.ListingId != testIds[0]), Is.True,
            "Should not include self in results");
    }

    [Test]
    public async Task Should_return_true_when_checking_exists_for_indexed_listing()
    {
        var testListingId = $"test-exists-{Guid.NewGuid():N}";
        var listing = new Listing
        {
            ListingId = testListingId,
            Title = "Test Listing for Exists Check",
            Description = "Testing the exists functionality"
        };

        await _service.IndexListings(new[] { listing });

        var exists = await _service.Exists(testListingId);
        Assert.That(exists, Is.True);
    }

    [Test]
    public async Task Should_return_false_when_checking_exists_for_non_existent_listing()
    {
        var nonExistentId = $"non-existent-{Guid.NewGuid():N}";

        var exists = await _service.Exists(nonExistentId);
        Assert.That(exists, Is.False);
    }

    [Test]
    public async Task Should_skip_listing_with_empty_title_and_description()
    {
        var emptyListing = new Listing
        {
            ListingId = $"test-empty-{Guid.NewGuid():N}",
            Title = "",
            Description = ""
        };

        var result = await _service.IndexListings(new[] { emptyListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
    }
}
