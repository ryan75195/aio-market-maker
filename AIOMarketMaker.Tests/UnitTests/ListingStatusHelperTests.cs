using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ListingStatusHelperTests
{
    [TestCase("Active", "Sold", true, Description = "Active to Sold allowed")]
    [TestCase("Active", "Ended", true, Description = "Active to Ended allowed")]
    [TestCase("Active", "OutOfStock", true, Description = "Active to OutOfStock allowed")]
    [TestCase("Active", "Active", true, Description = "Active to Active allowed (data refresh)")]
    [TestCase("Sold", "Active", false, Description = "Sold to Active blocked")]
    [TestCase("Sold", "Ended", false, Description = "Sold to Ended blocked")]
    [TestCase("Ended", "Active", false, Description = "Ended to Active blocked")]
    [TestCase("OutOfStock", "Active", false, Description = "OutOfStock to Active blocked")]
    [TestCase(null, "Active", true, Description = "Null to Active allowed")]
    [TestCase("Unknown", "Sold", true, Description = "Unknown status to Sold allowed")]
    public void Should_enforce_status_hierarchy(string? existingStatus, string? newStatus, bool expected)
    {
        var result = ListingStatusHelper.CanUpdateStatus(existingStatus, newStatus);
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase("Active", 0)]
    [TestCase("OutOfStock", 1)]
    [TestCase("Ended", 2)]
    [TestCase("Sold", 3)]
    [TestCase("Unknown", -1)]
    [TestCase(null, -1)]
    public void Should_return_correct_status_rank(string? status, int expectedRank)
    {
        var result = ListingStatusHelper.GetStatusRank(status);
        Assert.That(result, Is.EqualTo(expectedRank));
    }
}
