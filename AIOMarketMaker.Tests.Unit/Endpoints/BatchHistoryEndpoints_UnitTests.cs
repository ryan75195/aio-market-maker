using AIOMarketMaker.Api.Endpoints;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class BatchHistoryEndpoints_UnitTests
{
    [Test]
    public void Should_derive_Completed_when_all_runs_completed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Completed", "Completed" });
        Assert.That(result, Is.EqualTo("Completed"));
    }

    [Test]
    public void Should_derive_Failed_when_all_runs_failed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Failed", "Failed" });
        Assert.That(result, Is.EqualTo("Failed"));
    }

    [Test]
    public void Should_derive_PartialFailure_when_mix_of_completed_and_failed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Failed", "Completed" });
        Assert.That(result, Is.EqualTo("PartialFailure"));
    }

    [Test]
    public void Should_derive_Running_when_any_run_is_active()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Searching", "Queued" });
        Assert.That(result, Is.EqualTo("Running"));
    }

    [Test]
    public void Should_derive_Queued_when_all_queued()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Queued", "Queued" });
        Assert.That(result, Is.EqualTo("Queued"));
    }

    [TestCase("Running")]
    [TestCase("Searching")]
    [TestCase("Indexing")]
    [TestCase("Processing")]
    public void Should_derive_Running_for_active_status(string activeStatus)
    {
        var result = BatchStatusDeriver.Derive(new[] { activeStatus, "Completed" });
        Assert.That(result, Is.EqualTo("Running"));
    }

    [Test]
    public void Should_derive_Running_when_queued_mixed_with_completed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Queued" });
        Assert.That(result, Is.EqualTo("Running"));
    }
}
