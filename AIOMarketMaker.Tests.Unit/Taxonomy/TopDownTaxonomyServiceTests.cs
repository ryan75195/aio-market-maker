using AIOMarketMaker.Core.Services.Taxonomy;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TopDownTaxonomyServiceTests
{
    private Mock<ISkeletonGenerator> _skeletonGen = null!;
    private Mock<IExtractionModelRunner> _extractor = null!;
    private TopDownTaxonomyService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _skeletonGen = new Mock<ISkeletonGenerator>();
        _extractor = new Mock<IExtractionModelRunner>();
        _service = new TopDownTaxonomyService(_skeletonGen.Object, _extractor.Object);
    }

    [Test]
    public async Task Generate_Should_return_axes_from_skeleton()
    {
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("storage", "Storage capacity", new[] { "128gb", "256gb" }),
        });
        _skeletonGen.Setup(g => g.Generate(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(skeleton);
        _extractor.Setup(e => e.Extract(It.IsAny<string>(), skeleton))
            .ReturnsAsync(new Dictionary<string, string?> { ["storage"] = "256gb" });

        var result = await _service.Generate(new[] { "iPhone 256GB" }, "iPhone");

        Assert.That(result.Axes.Count(), Is.EqualTo(1));
        Assert.That(result.Axes.First().Name, Is.EqualTo("storage"));
    }

    [Test]
    public async Task Generate_Should_assign_listings_to_cells()
    {
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("storage", "Storage", new[] { "128gb", "256gb" }),
            new SkeletonAxis("color", "Color", new[] { "black", "white" }),
        });
        _skeletonGen.Setup(g => g.Generate(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(skeleton);

        _extractor.SetupSequence(e => e.Extract(It.IsAny<string>(), skeleton))
            .ReturnsAsync(new Dictionary<string, string?> { ["storage"] = "256gb", ["color"] = "black" })
            .ReturnsAsync(new Dictionary<string, string?> { ["storage"] = "256gb", ["color"] = "black" })
            .ReturnsAsync(new Dictionary<string, string?> { ["storage"] = "128gb", ["color"] = "white" })
            .ReturnsAsync((Dictionary<string, string?>?)null);

        var titles = new[] { "iPhone 256GB Black", "iPhone 256 Black Case", "iPhone 128 White", "iPhone Case Only" };
        var result = await _service.Generate(titles, "iPhone");

        Assert.Multiple(() =>
        {
            Assert.That(result.Assignments.Count(), Is.EqualTo(4));
            Assert.That(result.CoveragePercent, Is.EqualTo(75.0));
            Assert.That(result.ConflictPercent, Is.EqualTo(0.0));
            Assert.That(result.ExcludedCount, Is.EqualTo(1));

            var assigned = result.Assignments.Where(a => a.Cell.Count > 0).ToList();
            Assert.That(assigned, Has.Count.EqualTo(3));

            var cells = result.Cells.ToList();
            Assert.That(cells.Any(c => c.Count == 2), Is.True);
        });
    }

    [Test]
    public async Task Generate_Should_sample_titles_for_skeleton_when_many()
    {
        var skeleton = new ExtractionSkeleton(new[]
        {
            new SkeletonAxis("size", "Size", new[] { "small", "large" }),
        });
        _skeletonGen.Setup(g => g.Generate(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(skeleton);
        _extractor.Setup(e => e.Extract(It.IsAny<string>(), skeleton))
            .ReturnsAsync(new Dictionary<string, string?> { ["size"] = "small" });

        var titles = Enumerable.Range(0, 500).Select(i => $"Product {i}").ToList();
        await _service.Generate(titles, "Product");

        _skeletonGen.Verify(g => g.Generate(
            "Product",
            It.Is<IEnumerable<string>>(s => s.Count() <= 250),
            500,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
