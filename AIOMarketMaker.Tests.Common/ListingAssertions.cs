using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Tests.Common;

public static class ListingAssertions
{
    public static void AssertValidActiveListing(EbayProduct listing, string expectedId)
    {
        var validIsoOrSymbols = new[] { "GBP", "$", "£", "€", "USD", "EUR" };
        Assert.Multiple(() =>
        {
            // Identity
            Assert.That(listing, Is.Not.Null,
                "❌ Listing object was null");

            Assert.That(listing.ListingId, Is.EqualTo(expectedId),
                $"❌ Expected ListingId '{expectedId}', but got '{listing?.ListingId}'");

            Assert.That(listing.Url, Does.StartWith("https://").And.Contains(expectedId),
                $"❌ URL '{listing?.Url}' must start with https:// and contain '{expectedId}'");

            // Presence
            Assert.That(listing.Title, Is.Not.Null.And.Not.Empty,
                "❌ Title must not be null or empty");

            // Value checks
            Assert.That(listing.Price, Is.GreaterThan(0),
                $"❌ Price must be > 0 but was {listing.Price}");

            Assert.That(validIsoOrSymbols, Does.Contain(listing.Currency),
                $"❌ Currency '{listing.Currency}' is not in the allowed list");

            Assert.That(listing.ShippingCost, Is.GreaterThanOrEqualTo(0),
                $"❌ ShippingCost must be ≥ 0 but was {listing.ShippingCost}");

            Assert.That(listing.Images, Is.Not.Null.And.Not.Empty,
                "❌ Images list must not be null or empty");

            Assert.That(listing.ListingStatus, Is.EqualTo(EbayListingStatus.Active),
                "❌ Listing status should be active");
        });
    }

    public static void AssertValidSoldListing(EbayProduct listing, string expectedId)
    {
        var validIsoOrSymbols = new[] { "GBP", "$", "£", "€", "USD", "EUR" };
        {
            Assert.Multiple(() =>
            {
                // Identity
                Assert.That(listing, Is.Not.Null,
                    "❌ Listing object was null");

                Assert.That(listing.ListingId, Is.EqualTo(expectedId),
                    $"❌ Expected ListingId '{expectedId}', but got '{listing?.ListingId}'");

                Assert.That(listing.Url, Does.StartWith("https://").And.Contains(expectedId),
                    $"❌ URL '{listing?.Url}' must start with https:// and contain '{expectedId}'");

                // Presence
                Assert.That(listing.Title, Is.Not.Null.And.Not.Empty,
                    "❌ Title must not be null or empty");

                // Value checks
                Assert.That(listing.Price, Is.GreaterThan(0),
                    $"❌ Price must be > 0 but was {listing.Price}");

                Assert.That(validIsoOrSymbols, Does.Contain(listing.Currency),
                    $"❌ Currency '{listing.Currency}' is not in the allowed list");

                Assert.That(listing.ShippingCost, Is.GreaterThanOrEqualTo(0),
                    $"❌ ShippingCost must be ≥ 0 but was {listing.ShippingCost}");

                Assert.That(listing.Images, Is.Not.Null.And.Not.Empty,
                    "❌ Images list must not be null or empty");

                Assert.That(listing.ListingStatus, Is.EqualTo(EbayListingStatus.Sold),
                    "❌ Listing status should be sold");

                Assert.That(listing.EndDateUtc, Is.Not.Null,
                    "❌ Sold listings should contain an end date");
            });

        }
    }
}
