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

    [Test]
    public async Task Should_fetch_full_listing_details()
    {
        // Arrange - use listing ID that maps to our mock HTML
        var listingId = "306278488042"; // Maps to ActiveBuyItNowListing.htm

        // Act
        var results = await EbayScraper.GetItemsFromListings(new[] { listingId });

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1), "Should return exactly one listing");

        var listing = resultList.First();
        Assert.That(listing.ListingId, Is.EqualTo(listingId), "ListingId should match");
        Assert.That(listing.Title, Is.Not.Null.And.Not.Empty, "Should have a title");
        Assert.That(listing.Price, Is.GreaterThan(0), "Should have a price");
    }

    [Test]
    public async Task Should_handle_nonexistent_listing_gracefully()
    {
        // Arrange - use listing ID that doesn't exist in mock
        var nonexistentId = "999999999999";

        // Act
        var results = await EbayScraper.GetItemsFromListings(new[] { nonexistentId });

        // Assert - should return empty or handle gracefully, not throw
        var resultList = results.ToList();
        Assert.That(resultList, Is.Empty.Or.All.Matches<AIOMarketMaker.Models.Ebay.EbayProduct>(
            p => p.ListingId == nonexistentId),
            "Should either return empty or return item with ID but possibly null fields");
    }
}
