using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Core.Services.EntityResolution;
using AIOMarketMaker.Core.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAI;

namespace AIOMarketMaker.Tests.Integration;

/// <summary>
/// Live integration tests for OpenAI entity resolution.
/// These tests make real API calls - use sparingly and only for debugging.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("OpenAI")]
[Explicit("Makes real OpenAI API calls - run manually")]
public class OpenAiEntityResolutionIntegrationTests
{
    private OpenAiSettings _settings = null!;
    private OpenAIClient _client = null!;
    private PromptBuilder _promptBuilder = null!;
    private ILogger<OpenAiEntityResolutionService> _logger = null!;
    private OpenAiEntityResolutionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        // Load settings from environment or hardcode for testing
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? "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"; // Fallback to local.settings key

        _settings = new OpenAiSettings
        {
            ApiKey = apiKey,
            Model = "gpt-4o-mini", // Use gpt-4o-mini which is a real model
            BatchSize = 5,
            MaxRetries = 2,
            TimeoutSeconds = 60
        };

        _client = new OpenAIClient(_settings.ApiKey);
        _promptBuilder = new PromptBuilder();

        // Create a real logger that outputs to console
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        _logger = loggerFactory.CreateLogger<OpenAiEntityResolutionService>();

        var noOpIndexer = new NoOpProductNameIndexer();
        _service = new OpenAiEntityResolutionService(_client, _settings, _promptBuilder, noOpIndexer, _logger);
    }

    private static EbayProduct CreateProduct(
        string listingId,
        string title,
        decimal price,
        Condition condition,
        string itemSpecifics,
        string description)
    {
        return new EbayProduct(
            ListingId: listingId,
            Title: title,
            Price: price,
            Currency: "GBP",
            ShippingCost: null,
            Url: $"https://www.ebay.co.uk/itm/{listingId}",
            Condition: condition,
            Images: null,
            ListingStatus: EbayListingStatus.Active,
            PurchaseFormat: PurchaseFormat.BuyItNow,
            Description: description,
            ItemSpecifics: itemSpecifics,
            EndDateUtc: null,
            Location: "UK"
        );
    }

    [Test]
    public async Task ResolveAsync_WithSinglePS5Listing_ReturnsValidClassification()
    {
        // Arrange
        var products = new List<EbayProduct>
        {
            CreateProduct(
                "123456789",
                "Sony PlayStation 5 Console Disc Edition - White 825GB",
                450.00m,
                Condition.USED,
                "Brand: Sony, Model: PlayStation 5, Storage Capacity: 825GB, Color: White",
                "Lightly used PS5 disc edition in excellent condition. Includes original controller and cables."
            )
        };

        // Act
        Console.WriteLine("Calling OpenAI API...");
        var results = await _service.Resolve(products);

        // Assert
        Assert.That(results, Is.Not.Null);
        Assert.That(results.Count, Is.EqualTo(1));

        var result = results[0];
        Console.WriteLine($"ListingId: {result.ListingId}");
        Console.WriteLine($"Category: {result.Category}");
        Console.WriteLine($"Confidence: {result.CategoryConfidence}");
        Console.WriteLine($"Brand: {result.Attributes.Brand}");
        Console.WriteLine($"Model: {result.Attributes.Model}");
        Console.WriteLine($"Storage: {result.Attributes.StorageCapacity}");
        Console.WriteLine($"Color: {result.Attributes.Color}");

        Assert.That(result.ListingId, Is.EqualTo("123456789"));
        Assert.That(result.Category, Is.EqualTo("base_product"));
        Assert.That(result.Attributes.Brand, Is.EqualTo("Sony").IgnoreCase);
    }

    [Test]
    public async Task ResolveAsync_WithBundle_ReturnsValidClassification()
    {
        // Arrange
        var products = new List<EbayProduct>
        {
            CreateProduct(
                "987654321",
                "PS5 Bundle - Console + 2 Controllers + 5 Games + Headset",
                650.00m,
                Condition.USED,
                "Brand: Sony, Model: PlayStation 5",
                "Complete PS5 bundle includes: PS5 Disc Edition console, 2 DualSense controllers, Spider-Man 2, God of War Ragnarok, Horizon Forbidden West, Gran Turismo 7, Ratchet and Clank, and a wireless gaming headset."
            )
        };

        // Act
        Console.WriteLine("Calling OpenAI API for bundle...");
        var results = await _service.Resolve(products);

        // Assert
        Assert.That(results, Is.Not.Null);
        Assert.That(results.Count, Is.EqualTo(1));

        var result = results[0];
        Console.WriteLine($"ListingId: {result.ListingId}");
        Console.WriteLine($"Category: {result.Category}");
        Console.WriteLine($"Confidence: {result.CategoryConfidence}");
        Console.WriteLine($"BundledItems: {string.Join(", ", result.BundledItems ?? [])}");

        Assert.That(result.ListingId, Is.EqualTo("987654321"));
        Assert.That(result.Category, Is.EqualTo("bundle"));
        Assert.That(result.BundledItems, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task ResolveAsync_WithAccessory_ReturnsValidClassification()
    {
        // Arrange
        var products = new List<EbayProduct>
        {
            CreateProduct(
                "555555555",
                "Sony DualSense Wireless Controller for PS5 - Midnight Black",
                55.00m,
                Condition.NEW,
                "Brand: Sony, Type: Controller, Color: Midnight Black",
                "Brand new sealed DualSense controller for PlayStation 5."
            )
        };

        // Act
        Console.WriteLine("Calling OpenAI API for accessory...");
        var results = await _service.Resolve(products);

        // Assert
        Assert.That(results, Is.Not.Null);
        Assert.That(results.Count, Is.EqualTo(1));

        var result = results[0];
        Console.WriteLine($"ListingId: {result.ListingId}");
        Console.WriteLine($"Category: {result.Category}");
        Console.WriteLine($"Confidence: {result.CategoryConfidence}");

        Assert.That(result.ListingId, Is.EqualTo("555555555"));
        Assert.That(result.Category, Is.EqualTo("accessory"));
    }

    [Test]
    public async Task ResolveAsync_WithMultipleProducts_ReturnsAllClassifications()
    {
        // Arrange
        var products = new List<EbayProduct>
        {
            CreateProduct(
                "111111111",
                "Sony PlayStation 5 Digital Edition Console",
                380.00m,
                Condition.USED,
                "Brand: Sony, Model: PlayStation 5 Digital Edition",
                "PS5 Digital Edition in great condition."
            ),
            CreateProduct(
                "222222222",
                "PS5 Empty Box Only - No Console",
                25.00m,
                Condition.USED,
                "Type: Empty Box",
                "Original PlayStation 5 box only. No console or accessories included."
            ),
            CreateProduct(
                "333333333",
                "Spider-Man 2 PS5 Game",
                45.00m,
                Condition.NEW,
                "Platform: PlayStation 5, Game Name: Spider-Man 2",
                "Brand new sealed copy of Spider-Man 2 for PS5."
            )
        };

        // Act
        Console.WriteLine("Calling OpenAI API for multiple products...");
        var results = await _service.Resolve(products);

        // Assert
        Assert.That(results, Is.Not.Null);
        Assert.That(results.Count, Is.EqualTo(3));

        foreach (var result in results)
        {
            Console.WriteLine($"ListingId: {result.ListingId}, Category: {result.Category}, Confidence: {result.CategoryConfidence}");
        }

        // Verify each product
        var consoleResult = results.First(r => r.ListingId == "111111111");
        Assert.That(consoleResult.Category, Is.EqualTo("base_product"));

        var boxResult = results.First(r => r.ListingId == "222222222");
        Assert.That(boxResult.Category, Is.EqualTo("packaging_only"));

        var gameResult = results.First(r => r.ListingId == "333333333");
        Assert.That(gameResult.Category, Is.EqualTo("media"));
    }

    [Test]
    public async Task TestPromptAndRawResponse()
    {
        // This test just shows what we're sending and what we get back
        var products = new List<EbayProduct>
        {
            CreateProduct(
                "TEST001",
                "Sony PlayStation 5 Disc Edition",
                400.00m,
                Condition.USED,
                "Brand: Sony",
                "Test listing"
            )
        };

        var userPrompt = _promptBuilder.BuildUserPrompt(products);
        Console.WriteLine("=== SYSTEM PROMPT ===");
        Console.WriteLine(_promptBuilder.SystemPrompt);
        Console.WriteLine();
        Console.WriteLine("=== USER PROMPT ===");
        Console.WriteLine(userPrompt);
        Console.WriteLine();

        // Make raw API call to see exact response
        var chatClient = _client.GetChatClient(_settings.Model);
        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            new OpenAI.Chat.SystemChatMessage(_promptBuilder.SystemPrompt),
            new OpenAI.Chat.UserChatMessage(userPrompt)
        };

        var options = new OpenAI.Chat.ChatCompletionOptions
        {
            ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonObjectFormat()
        };

        Console.WriteLine("Calling OpenAI...");
        var response = await chatClient.CompleteChatAsync(messages, options);
        var content = response.Value.Content[0].Text;

        Console.WriteLine("=== RAW RESPONSE ===");
        Console.WriteLine(content);
        Console.WriteLine();

        // Now try to parse it
        Console.WriteLine("=== PARSED RESULT ===");
        var results = await _service.Resolve(products);
        foreach (var r in results)
        {
            Console.WriteLine($"ListingId: {r.ListingId}");
            Console.WriteLine($"Category: {r.Category}");
            Console.WriteLine($"Confidence: {r.CategoryConfidence}");
            Console.WriteLine($"Brand: {r.Attributes.Brand}");
            Console.WriteLine($"Model: {r.Attributes.Model}");
        }
    }
}
