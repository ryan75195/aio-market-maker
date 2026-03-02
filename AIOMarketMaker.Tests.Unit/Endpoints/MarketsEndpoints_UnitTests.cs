using AIOMarketMaker.Api.Endpoints;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class MarketsEndpoints_UnitTests
{
    [Test]
    public void Should_compute_sell_through_from_active_and_sold()
    {
        var result = MarketsCalc.SellThrough(active: 600, sold: 400);
        Assert.That(result, Is.EqualTo(40));
    }

    [Test]
    public void Should_compute_sales_per_day_from_sold_and_lookback()
    {
        var result = MarketsCalc.SalesPerDay(sold: 1800, lookbackDays: 180);
        Assert.That(result, Is.EqualTo(10.0m));
    }

    [Test]
    public void Should_return_zero_sell_through_when_no_listings()
    {
        var result = MarketsCalc.SellThrough(active: 0, sold: 0);
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void Should_return_zero_sales_per_day_when_lookback_is_zero()
    {
        var result = MarketsCalc.SalesPerDay(sold: 100, lookbackDays: 0);
        Assert.That(result, Is.EqualTo(0m));
    }

    [Test]
    public void Should_round_sell_through_to_nearest_integer()
    {
        // 333 / (333 + 667) = 0.333 => 33%
        var result = MarketsCalc.SellThrough(active: 667, sold: 333);
        Assert.That(result, Is.EqualTo(33));
    }

    [Test]
    public void Should_round_sales_per_day_to_one_decimal()
    {
        // 7 / 3 = 2.333... => 2.3
        var result = MarketsCalc.SalesPerDay(sold: 7, lookbackDays: 3);
        Assert.That(result, Is.EqualTo(2.3m));
    }

    [Test]
    public void Should_compute_days_on_market_for_sold_listing()
    {
        var created = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var ended = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = MarketsCalc.DaysOnMarket(created, ended);

        Assert.That(result, Is.EqualTo(14));
    }

    [Test]
    public void Should_compute_days_on_market_for_active_listing_using_now()
    {
        var created = DateTime.UtcNow.AddDays(-7);

        var result = MarketsCalc.DaysOnMarket(created, endDate: null);

        Assert.That(result, Is.EqualTo(7));
    }
}
