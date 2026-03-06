using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class LouvainCommunityDetectorTests
{
    private LouvainCommunityDetector _detector;

    [SetUp]
    public void SetUp()
    {
        _detector = new LouvainCommunityDetector();
    }

    [Test]
    public void Should_return_empty_for_empty_graph()
    {
        var result = _detector.Detect(
            Enumerable.Empty<WeightedEdge>(), nodeCount: 0).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_separate_disconnected_components()
    {
        // Two disconnected triangles: {0,1,2} and {3,4,5}
        var edges = new[]
        {
            new WeightedEdge(0, 1, 1.0), new WeightedEdge(0, 2, 1.0),
            new WeightedEdge(1, 2, 1.0),
            new WeightedEdge(3, 4, 1.0), new WeightedEdge(3, 5, 1.0),
            new WeightedEdge(4, 5, 1.0),
        };

        var result = _detector.Detect(edges, nodeCount: 6).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_return_single_community_for_fully_connected_graph()
    {
        // Complete graph with 4 nodes
        var edges = new[]
        {
            new WeightedEdge(0, 1, 1.0), new WeightedEdge(0, 2, 1.0),
            new WeightedEdge(0, 3, 1.0), new WeightedEdge(1, 2, 1.0),
            new WeightedEdge(1, 3, 1.0), new WeightedEdge(2, 3, 1.0),
        };

        // Resolution 1.0 favors merging dense clusters into one community
        var result = _detector.Detect(edges, nodeCount: 4, resolution: 1.0).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        var totalMembers = result.Sum(c => c.Members.Count());
        Assert.That(totalMembers, Is.EqualTo(4));
    }

    [Test]
    public void Should_split_weakly_connected_groups_at_high_resolution()
    {
        // Two tight clusters connected by a weak bridge
        var edges = new List<WeightedEdge>
        {
            new(0, 1, 1.0), new(0, 2, 1.0), new(1, 2, 1.0),
            new(3, 4, 1.0), new(3, 5, 1.0), new(4, 5, 1.0),
            new(2, 3, 0.1),
        };

        var result = _detector.Detect(edges, nodeCount: 6, resolution: 2.0).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }

    [Test]
    public void Should_assign_community_ids_starting_from_zero()
    {
        var edges = new[]
        {
            new WeightedEdge(0, 1, 1.0),
            new WeightedEdge(2, 3, 1.0),
        };

        var result = _detector.Detect(edges, nodeCount: 4).ToList();

        var ids = result.Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.That(ids[0], Is.EqualTo(0));
        Assert.That(ids[1], Is.EqualTo(1));
    }

    [Test]
    public void Should_respect_edge_weights()
    {
        var edges = new[]
        {
            new WeightedEdge(0, 1, 1.0), new WeightedEdge(1, 2, 1.0),
            new WeightedEdge(0, 2, 1.0),
            new WeightedEdge(1, 3, 0.05),
            new WeightedEdge(3, 4, 1.0), new WeightedEdge(3, 5, 1.0),
            new WeightedEdge(4, 5, 1.0),
        };

        var result = _detector.Detect(edges, nodeCount: 6, resolution: 2.0).ToList();

        var communityWith0 = result.First(c =>
            c.Members.Any(m => m.Canonical == "0"));
        Assert.That(
            communityWith0.Members.Any(m => m.Canonical == "1"),
            Is.True);
    }
}
