using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("Unit")]
public class TestableEbayUrlBuilder_Tests
{
    [Test]
    public void Should_build_listing_url_with_custom_base()
    {
        // Arrange
        var builder = new TestableEbayUrlBuilder("http://localhost:9999");

        // Act
        var url = builder.BuildListingUrl("123456789");

        // Assert
        Assert.That(url, Is.EqualTo("http://localhost:9999/itm/123456789"));
    }

    [Test]
    public void Should_build_search_url_with_custom_base()
    {
        // Arrange
        var builder = new TestableEbayUrlBuilder("http://localhost:9999");

        // Act
        var url = builder.BuildSearchUrl("test query", sold: false, page: 1, Condition.USED, BuyingFormat.BUY_NOW);

        // Assert
        Assert.That(url, Does.StartWith("http://localhost:9999/sch/i.html"));
        Assert.That(url, Does.Contain("_nkw=test+query"));
        Assert.That(url, Does.Contain("LH_BIN=1")); // Buy it now flag
    }

    [Test]
    public void Should_build_sold_search_url_with_sold_flags()
    {
        // Arrange
        var builder = new TestableEbayUrlBuilder("http://localhost:9999");

        // Act
        var url = builder.BuildSearchUrl("test", sold: true, page: 1, Condition.USED, BuyingFormat.BUY_NOW);

        // Assert
        Assert.That(url, Does.Contain("LH_Sold=1"));
        Assert.That(url, Does.Contain("LH_Complete=1"));
    }

    [Test]
    public void Should_trim_trailing_slash_from_base_url()
    {
        // Arrange
        var builder = new TestableEbayUrlBuilder("http://localhost:9999/");

        // Act
        var url = builder.BuildListingUrl("123");

        // Assert
        Assert.That(url, Is.EqualTo("http://localhost:9999/itm/123"));
        Assert.That(url, Does.Not.Contain("//itm")); // No double slash
    }
}
