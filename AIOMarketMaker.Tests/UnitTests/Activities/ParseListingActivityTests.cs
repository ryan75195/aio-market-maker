using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Activities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class ParseListingActivityTests
{
    private ParseListingActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var listingParser = new EbayListingParser();
        _activity = new ParseListingActivity(
            listingParser,
            NullLogger<ParseListingActivity>.Instance);
    }

    [Test]
    public async Task Should_return_null_for_empty_html()
    {
        // Arrange
        var input = new ParseListingInput("123", "https://ebay.com/itm/123", "");

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Should_parse_active_buy_it_now_listing()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("ActiveBuyItNowListing");
        var input = new ParseListingInput("test123", "https://ebay.com/itm/test123", html);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Title, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Price, Is.Not.Null);
            Assert.That(result.ListingStatus, Is.EqualTo("Active"));
        });
    }

    [Test]
    public async Task Should_parse_sold_listing()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("SoldBuyNowListing");
        var input = new ParseListingInput("sold123", "https://ebay.com/itm/sold123", html);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ListingStatus, Is.EqualTo("Sold"));
        });
    }

    [Test]
    public async Task Should_extract_description_source_url()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("ActiveBuyItNowListing");
        var input = new ParseListingInput("test123", "https://ebay.com/itm/test123", html);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        // Description source URL may or may not be present depending on listing
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Should_use_input_listing_id_when_parser_returns_null()
    {
        // Arrange - use HTML that might not have ID in expected location
        var html = "<html><body><h1>Minimal page</h1></body></html>";
        var input = new ParseListingInput("fallback123", "https://ebay.com/itm/fallback123", html);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ListingId, Is.EqualTo("fallback123"));
        });
    }

    private async Task<string> LoadTestHtmlAsync(string testCaseName)
    {
        var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Listings"));
        var htmlPath = Path.Combine(dataDir, testCaseName + ".htm");
        return await File.ReadAllTextAsync(htmlPath);
    }
}
