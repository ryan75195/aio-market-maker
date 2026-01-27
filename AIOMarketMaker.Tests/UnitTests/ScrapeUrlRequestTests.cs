using NUnit.Framework;
using AIOMarketMaker.Core.Models;

namespace AIOMarketMaker.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ScrapeUrlRequest_UnitTests
{
    [Test]
    public void Should_store_all_properties()
    {
        var request = new ScrapeUrlRequest
        {
            Url = "https://ebay.com/itm/123",
            GroupId = "123",
            FileKey = "listing"
        };

        Assert.Multiple(() =>
        {
            Assert.That(request.Url, Is.EqualTo("https://ebay.com/itm/123"));
            Assert.That(request.GroupId, Is.EqualTo("123"));
            Assert.That(request.FileKey, Is.EqualTo("listing"));
        });
    }

    [Test]
    public void Should_allow_null_GroupId_and_FileKey()
    {
        var request = new ScrapeUrlRequest
        {
            Url = "https://ebay.com/itm/123"
        };

        Assert.Multiple(() =>
        {
            Assert.That(request.GroupId, Is.Null);
            Assert.That(request.FileKey, Is.Null);
        });
    }
}
