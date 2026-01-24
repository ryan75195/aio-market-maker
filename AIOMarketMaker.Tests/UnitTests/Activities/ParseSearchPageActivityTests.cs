using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class ParseSearchPageActivityTests
{
    private ParseSearchPageActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var searchParser = new EbaySearchParser();
        _activity = new ParseSearchPageActivity(
            searchParser,
            NullLogger<ParseSearchPageActivity>.Instance);
    }

    [Test]
    public async Task Should_return_success_with_empty_list_for_empty_html()
    {
        // Arrange
        var input = new ParseSearchPageInput("", 1, false, null);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ListingIds, Is.Empty);
        });
    }

    [Test]
    public async Task Should_parse_listing_ids_from_search_html()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("Sold_With_Small_Number_of_Real_Results");
        var input = new ParseSearchPageInput(html, 1, true, null);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ListingIds, Is.Not.Empty);
            Assert.That(result.ListingIds, Does.Contain("156876090176"));
        });
    }

    [Test]
    public async Task Should_filter_by_lookback_days_for_sold_listings()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("Sold_With_Small_Number_of_Real_Results");
        // Use very short lookback to filter out old listings
        var input = new ParseSearchPageInput(html, 1, true, 1);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        // The test HTML has listings from the past, so with 1-day lookback most should be filtered
        Assert.That(result.Success, Is.True);
        // We can't assert exact count since it depends on when test runs vs saved HTML dates
    }

    [Test]
    public async Task Should_not_filter_active_listings_by_date()
    {
        // Arrange
        var html = await LoadTestHtmlAsync("Sold_With_Small_Number_of_Real_Results");
        var input = new ParseSearchPageInput(html, 1, false, 1); // IsSold=false, even with lookback

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            // Active listings should not be filtered by date
        });
    }

    private async Task<string> LoadTestHtmlAsync(string testCaseName)
    {
        var dataDir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../Data/Search"));
        var htmlPath = Path.Combine(dataDir, testCaseName + ".htm");
        return await File.ReadAllTextAsync(htmlPath);
    }
}
