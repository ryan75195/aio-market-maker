using NUnit.Framework;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Tests.Unit.Data;

[TestFixture]
[Category("Unit")]
public class DbQueryHelperTests
{
    [Test]
    public void Should_parse_job_ids_from_comma_separated_string()
    {
        var result = DbQueryHelper.ParseJobIds("1,2,3").ToList();
        Assert.That(result, Is.EqualTo(new List<int> { 1, 2, 3 }));
    }

    [Test]
    public void Should_return_empty_list_for_null_input()
    {
        var result = DbQueryHelper.ParseJobIds(null).ToList();
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_skip_invalid_ids_in_comma_separated_string()
    {
        var result = DbQueryHelper.ParseJobIds("1,abc,3,,5").ToList();
        Assert.That(result, Is.EqualTo(new List<int> { 1, 3, 5 }));
    }

    [Test]
    public void Should_return_empty_list_for_empty_string()
    {
        var result = DbQueryHelper.ParseJobIds("").ToList();
        Assert.That(result, Is.Empty);
    }
}
