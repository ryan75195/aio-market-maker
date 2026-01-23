using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("E2E")]
public class ScrapePipeline_E2ETests : E2ETestFixture
{
    [Test]
    public async Task Should_search_active_listings_and_return_results()
    {
        // Act - search using mock eBay server
        var results = await EbayScraper.SearchActiveListings(
            "test",
            BuyingFormat.BUY_NOW,
            Condition.USED,
            itemLimit: 10);

        // Assert - verify we got parsed results
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty, "Should return at least one listing from mock HTML");
        Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
            "All results should have a ListingId");
        Assert.That(resultList.All(r => r.Price > 0), Is.True,
            "All results should have a price > 0");
    }

    [Test]
    public async Task Should_search_sold_listings_with_date_filter()
    {
        // Arrange - use date range that matches the mock HTML data
        var startDate = new DateTime(2025, 4, 1);
        var endDate = new DateTime(2025, 5, 15);

        // Act
        var results = await EbayScraper.SearchSoldListings(
            "test",
            BuyingFormat.BUY_NOW,
            Condition.USED,
            startDate,
            endDate);

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty, "Should return sold listings from mock HTML");
        Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
            "All results should have a ListingId");
        Assert.That(resultList.All(r => r.EndDateUtc >= startDate && r.EndDateUtc <= endDate), Is.True,
            "All results should be within date range");
    }
}
