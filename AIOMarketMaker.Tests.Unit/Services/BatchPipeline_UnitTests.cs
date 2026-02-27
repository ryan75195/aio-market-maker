using System.Text.Json;
using AIOMarketMaker.Models.Ebay;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class BatchPipeline_UnitTests
{
    [Test]
    public void Should_round_trip_search_results_through_json_serialization()
    {
        var summaries = new List<EbayProductSummary>
        {
            new(
                ListingId: "123456",
                Title: "iPhone 15 Pro 256GB",
                Price: 899.99m,
                Currency: "GBP",
                ShippingCost: 4.99m,
                Url: "https://www.ebay.co.uk/itm/123456",
                BuyingFormat: BuyingFormat.BUY_NOW,
                Condition: Condition.NEW,
                Images: new[] { "https://img.ebay.com/1.jpg", "https://img.ebay.com/2.jpg" },
                EndDateUtc: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
                IsSold: true
            ),
            new(
                ListingId: "789012",
                Title: "Samsung Galaxy S24",
                Price: 650.00m,
                Currency: "GBP",
                ShippingCost: null,
                Url: "https://www.ebay.co.uk/itm/789012",
                BuyingFormat: BuyingFormat.AUCTION,
                Condition: Condition.USED,
                Images: null,
                EndDateUtc: null,
                IsSold: false
            )
        };

        var json = JsonSerializer.Serialize(summaries);
        var deserialized = JsonSerializer.Deserialize<List<EbayProductSummary>>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized, Has.Count.EqualTo(2));

        var sold = deserialized![0];
        Assert.Multiple(() =>
        {
            Assert.That(sold.ListingId, Is.EqualTo("123456"));
            Assert.That(sold.Title, Is.EqualTo("iPhone 15 Pro 256GB"));
            Assert.That(sold.Price, Is.EqualTo(899.99m));
            Assert.That(sold.ShippingCost, Is.EqualTo(4.99m));
            Assert.That(sold.BuyingFormat, Is.EqualTo(BuyingFormat.BUY_NOW));
            Assert.That(sold.Condition, Is.EqualTo(Condition.NEW));
            Assert.That(sold.Images, Is.Not.Null);
            Assert.That(sold.Images!.Count(), Is.EqualTo(2));
            Assert.That(sold.IsSold, Is.True);
            Assert.That(sold.EndDateUtc, Is.EqualTo(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)));
        });

        var active = deserialized[1];
        Assert.Multiple(() =>
        {
            Assert.That(active.ListingId, Is.EqualTo("789012"));
            Assert.That(active.ShippingCost, Is.Null);
            Assert.That(active.Images, Is.Null);
            Assert.That(active.EndDateUtc, Is.Null);
            Assert.That(active.IsSold, Is.False);
            Assert.That(active.BuyingFormat, Is.EqualTo(BuyingFormat.AUCTION));
            Assert.That(active.Condition, Is.EqualTo(Condition.USED));
        });
    }

    [Test]
    public void Should_split_deserialized_results_into_sold_and_active()
    {
        var summaries = new List<EbayProductSummary>
        {
            new("1", "Sold Item", 100m, "GBP", null, null, null, null, null, null, IsSold: true),
            new("2", "Active Item", 200m, "GBP", null, null, null, null, null, null, IsSold: false),
            new("3", "Another Sold", 150m, "GBP", null, null, null, null, null, null, IsSold: true),
        };

        var json = JsonSerializer.Serialize(summaries);
        var deserialized = JsonSerializer.Deserialize<List<EbayProductSummary>>(json)!;

        var sold = deserialized.Where(s => s.IsSold).ToList();
        var active = deserialized.Where(s => !s.IsSold).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(sold, Has.Count.EqualTo(2));
            Assert.That(active, Has.Count.EqualTo(1));
            Assert.That(sold[0].ListingId, Is.EqualTo("1"));
            Assert.That(sold[1].ListingId, Is.EqualTo("3"));
            Assert.That(active[0].ListingId, Is.EqualTo("2"));
        });
    }

    [Test]
    public void Should_handle_empty_search_results()
    {
        var summaries = new List<EbayProductSummary>();

        var json = JsonSerializer.Serialize(summaries);
        var deserialized = JsonSerializer.Deserialize<List<EbayProductSummary>>(json);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized, Is.Empty);
    }
}
