using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires valid Pinecone API key and index in local.settings.json")]
public class SemanticSearchServiceIntegrationTests
{
    private ISemanticSearchService _service = null!;
    private IEmbeddingService _embeddingService = null!;

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
        var pineconeKey = configuration.GetValue<string>("Pinecone:ApiKey");

        if (string.IsNullOrEmpty(openAiKey) || string.IsNullOrEmpty(pineconeKey))
        {
            Assert.Ignore("OpenAi:ApiKey or Pinecone:ApiKey not found in local.settings.json");
        }

        var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-large";
        var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 3072);
        var embeddingConfig = new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions);
        var embeddingLogger = new Mock<ILogger<EmbeddingService>>();
        _embeddingService = new EmbeddingService(embeddingConfig, embeddingLogger.Object);

        var indexName = configuration.GetValue<string>("Pinecone:IndexName") ?? "arbitrage";
        var topK = configuration.GetValue<int>("Pinecone:TopK", 10);
        var pineconeConfig = new PineconeConfig(pineconeKey, indexName, topK);
        var pineconeClient = new PineconeIndexClientWrapper(pineconeKey, indexName);
        var searchLogger = new Mock<ILogger<SemanticSearchService>>();
        _service = new SemanticSearchService(pineconeConfig, pineconeClient, _embeddingService, searchLogger.Object);
    }

    [Test]
    public async Task Should_return_zero_counts_when_indexing_empty_list()
    {
        var result = await _service.IndexListingsAsync(Array.Empty<Listing>());

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
            await _service.SearchAsync(""));
    }

    [Test]
    public async Task Should_succeed_when_deleting_empty_list()
    {
        await _service.DeleteAsync(Array.Empty<string>());
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

        try
        {
            var indexResult = await _service.IndexListingsAsync(new[] { listing });
            Assert.That(indexResult.UpsertedCount, Is.EqualTo(1));

            await Task.Delay(2000);

            var searchResult = await _service.SearchAsync("PS5 console disc version");
            Assert.That(searchResult.Hits.Any(h => h.ListingId == testListingId), Is.True,
                "Search should find the indexed listing");
        }
        finally
        {
            await _service.DeleteAsync(new[] { testListingId });
        }
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

        try
        {
            var indexResult = await _service.IndexListingsAsync(listings);
            Assert.That(indexResult.UpsertedCount, Is.EqualTo(2));

            await Task.Delay(2000);

            var similarResult = await _service.FindSimilarAsync(testIds[0]);
            Assert.That(similarResult.Hits.Any(h => h.ListingId == testIds[1]), Is.True,
                "Should find similar listing");
            Assert.That(similarResult.Hits.All(h => h.ListingId != testIds[0]), Is.True,
                "Should not include self in results");
        }
        finally
        {
            await _service.DeleteAsync(testIds);
        }
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

        try
        {
            await _service.IndexListingsAsync(new[] { listing });
            await Task.Delay(2000);

            var exists = await _service.ExistsAsync(testListingId);
            Assert.That(exists, Is.True);
        }
        finally
        {
            await _service.DeleteAsync(new[] { testListingId });
        }
    }

    [Test]
    public async Task Should_return_false_when_checking_exists_for_non_existent_listing()
    {
        var nonExistentId = $"non-existent-{Guid.NewGuid():N}";

        var exists = await _service.ExistsAsync(nonExistentId);
        Assert.That(exists, Is.False);
    }

    [Test]
    public async Task Should_only_search_within_filtered_listing_ids()
    {
        var includedId = $"test-included-{Guid.NewGuid():N}";
        var excludedId = $"test-excluded-{Guid.NewGuid():N}";

        var listings = new[]
        {
            new Listing
            {
                ListingId = includedId,
                Title = "PlayStation 5 Console",
                Description = "PS5 gaming console"
            },
            new Listing
            {
                ListingId = excludedId,
                Title = "PlayStation 5 Console Similar",
                Description = "Another PS5 console"
            }
        };

        try
        {
            await _service.IndexListingsAsync(listings);
            await Task.Delay(2000);

            var result = await _service.SearchAsync(
                "PlayStation 5",
                filterToListingIds: new[] { includedId });

            Assert.Multiple(() =>
            {
                Assert.That(result.Hits.Any(h => h.ListingId == includedId), Is.True,
                    "Should find included listing");
                Assert.That(result.Hits.All(h => h.ListingId != excludedId), Is.True,
                    "Should not find excluded listing");
            });
        }
        finally
        {
            await _service.DeleteAsync(new[] { includedId, excludedId });
        }
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

        var result = await _service.IndexListingsAsync(new[] { emptyListing });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpsertedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
        });
    }
}
