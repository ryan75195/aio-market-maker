using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class UnionFindTests
{
    [Test]
    public void Should_keep_elements_separate_when_no_unions()
    {
        var uf = new UnionFind(3);

        var groups = uf.GetGroups().ToList();

        Assert.That(groups.Count, Is.EqualTo(3));
    }

    [Test]
    public void Should_merge_two_elements_into_one_group()
    {
        var uf = new UnionFind(3);

        uf.Union(0, 1);
        var groups = uf.GetGroups().ToList();

        Assert.That(groups.Count, Is.EqualTo(2));
        var merged = groups.First(g => g.Count > 1);
        Assert.That(merged, Does.Contain(0));
        Assert.That(merged, Does.Contain(1));
    }

    [Test]
    public void Should_handle_transitive_unions()
    {
        var uf = new UnionFind(4);

        uf.Union(0, 1);
        uf.Union(1, 2);

        Assert.That(uf.Find(0), Is.EqualTo(uf.Find(2)),
            "0 and 2 should be in the same group via transitivity through 1");
        var groups = uf.GetGroups().ToList();
        Assert.That(groups.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_handle_redundant_unions()
    {
        var uf = new UnionFind(3);

        uf.Union(0, 1);
        uf.Union(0, 1);
        uf.Union(1, 0);

        var groups = uf.GetGroups().ToList();
        Assert.That(groups.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_handle_single_element()
    {
        var uf = new UnionFind(1);

        var groups = uf.GetGroups().ToList();

        Assert.That(groups.Count, Is.EqualTo(1));
        Assert.That(groups[0], Does.Contain(0));
    }

    [Test]
    public void Should_merge_all_elements_into_one_group()
    {
        var uf = new UnionFind(5);

        for (var i = 0; i < 4; i++)
        {
            uf.Union(i, i + 1);
        }

        var groups = uf.GetGroups().ToList();
        Assert.That(groups.Count, Is.EqualTo(1));
        Assert.That(groups[0].Count, Is.EqualTo(5));
    }
}
