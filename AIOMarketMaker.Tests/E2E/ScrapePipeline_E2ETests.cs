using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("E2E")]
public class ScrapePipeline_E2ETests : E2ETestFixture
{
    [Test]
    public async Task Should_search_active_listings_and_parse_all_fields()
    {
        // Act - search using mock eBay server
        var results = await EbayScraper.SearchActiveListings(
            "test",
            BuyingFormat.BUY_NOW,
            Condition.USED,
            itemLimit: 10);

        // Assert - verify parser extracted key fields from search results
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty, "Should return at least one listing from mock HTML");

        // Log the first result for visibility
        var first = resultList.First();
        Console.WriteLine($"First result: ID={first.ListingId}, Title={first.Title}, Price={first.Price} {first.Currency}");

        Assert.Multiple(() =>
        {
            // All results should have core fields populated
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
                "All results should have a ListingId");
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.Title)), Is.True,
                "All results should have a Title");
            Assert.That(resultList.All(r => r.Price > 0), Is.True,
                "All results should have a Price > 0");
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.Currency)), Is.True,
                "All results should have a Currency");
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.Url)), Is.True,
                "All results should have a Url");

            // At least some results should have condition parsed
            Assert.That(resultList.Any(r => r.Condition != null), Is.True,
                "At least one result should have Condition parsed");
        });
    }

    [Test]
    public async Task Should_search_sold_listings_and_parse_end_dates()
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

        // Log the first result
        var first = resultList.First();
        Console.WriteLine($"First sold result: ID={first.ListingId}, Title={first.Title}, EndDate={first.EndDateUtc}");

        Assert.Multiple(() =>
        {
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
                "All results should have a ListingId");
            Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.Title)), Is.True,
                "All results should have a Title");
            Assert.That(resultList.All(r => r.EndDateUtc.HasValue), Is.True,
                "All sold results should have EndDateUtc parsed");
            Assert.That(resultList.All(r => r.EndDateUtc >= startDate && r.EndDateUtc <= endDate), Is.True,
                "All results should be within date range");
        });
    }

    [Test]
    public async Task Should_fetch_full_listing_and_parse_all_details()
    {
        // Arrange - use listing ID that maps to our mock HTML (ActiveBuyItNowListing.htm)
        var listingId = "306278488042";

        // Act
        var results = await EbayScraper.GetItemsFromListings(new[] { listingId });

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1), "Should return exactly one listing");

        var listing = resultList.First();

        // Log all parsed fields for visibility
        Console.WriteLine($"Parsed listing details:");
        Console.WriteLine($"  ListingId: {listing.ListingId}");
        Console.WriteLine($"  Title: {listing.Title}");
        Console.WriteLine($"  Price: {listing.Price} {listing.Currency}");
        Console.WriteLine($"  Condition: {listing.Condition}");
        Console.WriteLine($"  ListingStatus: {listing.ListingStatus}");
        Console.WriteLine($"  PurchaseFormat: {listing.PurchaseFormat}");
        Console.WriteLine($"  Images: {listing.Images?.Count() ?? 0}");
        Console.WriteLine($"  Description: {(listing.Description != null ? "present" : "null")}");
        Console.WriteLine($"  ShippingCost: {listing.ShippingCost}");

        Assert.Multiple(() =>
        {
            // Core fields
            Assert.That(listing.ListingId, Is.EqualTo(listingId), "ListingId should match");
            Assert.That(listing.Title, Is.Not.Null.And.Not.Empty, "Should have a title");
            Assert.That(listing.Price, Is.GreaterThan(0), "Should have a price > 0");
            Assert.That(listing.Currency, Is.Not.Null.And.Not.Empty, "Should have currency");
            Assert.That(listing.Url, Does.Contain(listingId), "URL should contain listing ID");

            // Listing metadata
            Assert.That(listing.ListingStatus, Is.EqualTo(EbayListingStatus.Active),
                "Active listing should have Active status");
            Assert.That(listing.PurchaseFormat, Is.Not.Null.And.Not.EqualTo(PurchaseFormat.Unknown),
                "Should parse purchase format");

            // Images should be extracted
            Assert.That(listing.Images, Is.Not.Null.And.Not.Empty,
                "Should have at least one image");
        });
    }

    [Test]
    public async Task Should_handle_nonexistent_listing_gracefully()
    {
        // Arrange - use listing ID that doesn't exist in mock
        var nonexistentId = "999999999999";

        // Act - should not throw even for nonexistent listing
        var results = await EbayScraper.GetItemsFromListings(new[] { nonexistentId });

        // Assert - should either return empty, return item with matching ID,
        // or return item with empty/null fields (parser gracefully handles missing data)
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.LessThanOrEqualTo(1),
            "Should return at most one result for one input");

        if (resultList.Count == 1)
        {
            // When parser can't extract data, it returns default/empty values
            var product = resultList.First();
            Assert.That(product, Is.Not.Null, "Product should not be null");
            // The URL should be preserved even if other fields are empty
            Assert.That(product.Url, Does.Contain(nonexistentId),
                "Product URL should contain the requested listing ID");
        }
    }
}
