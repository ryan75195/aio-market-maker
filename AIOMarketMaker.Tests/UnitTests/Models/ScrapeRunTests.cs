using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Tests.UnitTests.Models;

[TestFixture]
[Category("Unit")]
public class ScrapeRunTests
{
    [Test]
    public void Should_have_manual_trigger_type_by_default()
    {
        var run = new ScrapeRun();
        Assert.That(run.TriggerType, Is.EqualTo("Manual"));
    }

    [Test]
    public void Should_have_running_status_by_default()
    {
        var run = new ScrapeRun();
        Assert.That(run.Status, Is.EqualTo("Running"));
    }

    [Test]
    public void Should_have_zero_listings_added_by_default()
    {
        var run = new ScrapeRun();
        Assert.That(run.ListingsAdded, Is.EqualTo(0));
    }

    [Test]
    public void Should_have_zero_listings_skipped_by_default()
    {
        var run = new ScrapeRun();
        Assert.That(run.ListingsSkipped, Is.EqualTo(0));
    }

    [Test]
    public void Should_have_null_completed_utc_by_default()
    {
        var run = new ScrapeRun();
        Assert.That(run.CompletedUtc, Is.Null);
    }
}
