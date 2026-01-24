using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class BuildUrlsActivityTests
{
    private BuildUrlsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        var urlBuilder = new EbayUrlBuilder();
        _activity = new BuildUrlsActivity(urlBuilder);
    }

    [Test]
    public void Should_build_active_search_url()
    {
        // Arrange
        var input = new BuildSearchUrlInput("PlayStation 5", false, 1);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com"));
            Assert.That(url, Does.Contain("PlayStation"));
            Assert.That(url, Does.Not.Contain("LH_Sold=1"));
        });
    }

    [Test]
    public void Should_build_sold_search_url()
    {
        // Arrange
        var input = new BuildSearchUrlInput("PlayStation 5", true, 1);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com"));
            Assert.That(url, Does.Contain("LH_Sold=1"));
            Assert.That(url, Does.Contain("LH_Complete=1"));
        });
    }

    [Test]
    public void Should_build_search_url_with_page_number()
    {
        // Arrange
        var input = new BuildSearchUrlInput("test", false, 3);

        // Act
        var url = _activity.BuildSearchUrlActivity(input, null!);

        // Assert
        Assert.That(url, Does.Contain("_pgn=3"));
    }

    [Test]
    public void Should_build_listing_url_from_id()
    {
        // Arrange
        var listingId = "123456789012";

        // Act
        var url = _activity.BuildListingUrlActivity(listingId, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(url, Does.Contain("ebay.com/itm/"));
            Assert.That(url, Does.Contain(listingId));
        });
    }
}
