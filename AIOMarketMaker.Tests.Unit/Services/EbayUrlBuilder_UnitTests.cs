using NUnit.Framework;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class EbayUrlBuilder_UnitTests
{
    private EbayUrlBuilder _urlBuilder = null!;

    [SetUp]
    public void SetUp()
    {
        _urlBuilder = new EbayUrlBuilder();
    }

    [Test]
    public void BuildDescriptionUrl_Should_return_valid_description_url()
    {
        var result = _urlBuilder.BuildDescriptionUrl("306278488042");

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.StartWith("https://itm.ebaydesc.com/itmdesc/306278488042"));
            Assert.That(result, Does.Contain("excSoj=1"));
            Assert.That(result, Does.Contain("domain=ebay.com"));
        });
    }

    [Test]
    public void BuildDescriptionUrl_Should_throw_for_null_listingId()
    {
        Assert.Throws<ArgumentException>(() => _urlBuilder.BuildDescriptionUrl(null!));
    }

    [Test]
    public void BuildDescriptionUrl_Should_throw_for_empty_listingId()
    {
        Assert.Throws<ArgumentException>(() => _urlBuilder.BuildDescriptionUrl(""));
    }
}
