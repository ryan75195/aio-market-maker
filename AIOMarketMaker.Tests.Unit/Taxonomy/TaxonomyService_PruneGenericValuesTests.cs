using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyService_PruneGenericValuesTests
{
    [Test]
    public void Should_remove_stop_word_values_from_axis()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("warranty", new[] { new Ngram("warranty", new[] { "warranty" }, 30) }),
                new AxisValue("with", new[] { new Ngram("with", new[] { "with" }, 30) }),
                new AxisValue("sealed", new[] { new Ngram("sealed", new[] { "sealed" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Contain("warranty"));
        Assert.That(labels, Does.Contain("sealed"));
        Assert.That(labels, Does.Not.Contain("with"));
    }

    [Test]
    public void Should_remove_multiple_stop_words_from_same_axis()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("free", new[] { new Ngram("free", new[] { "free" }, 30) }),
                new AxisValue("all", new[] { new Ngram("all", new[] { "all" }, 30) }),
                new AxisValue("day", new[] { new Ngram("day", new[] { "day" }, 30) }),
                new AxisValue("black", new[] { new Ngram("black", new[] { "black" }, 30) }),
                new AxisValue("white", new[] { new Ngram("white", new[] { "white" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(1));
        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Is.EquivalentTo(new[] { "black", "white" }));
    }

    [Test]
    public void Should_drop_axis_when_all_values_are_stop_words()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("with", new[] { new Ngram("with", new[] { "with" }, 30) }),
                new AxisValue("free", new[] { new Ngram("free", new[] { "free" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_drop_axis_when_stop_word_removal_leaves_fewer_than_two_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("count", new[] { new Ngram("count", new[] { "count" }, 30) }),
                new AxisValue("with", new[] { new Ngram("with", new[] { "with" }, 30) }),
                new AxisValue("day", new[] { new Ngram("day", new[] { "day" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_not_remove_product_attribute_values()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("new", new[] { new Ngram("new", new[] { "new" }, 30) }),
                new AxisValue("used", new[] { new Ngram("used", new[] { "used" }, 30) }),
                new AxisValue("sealed", new[] { new Ngram("sealed", new[] { "sealed" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(3),
            "new/used/sealed are product attributes, not generic stop words");
    }

    [Test]
    public void Should_leave_axes_without_stop_words_unchanged()
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Values.Count(), Is.EqualTo(2));
    }

    [TestCase("with")]
    [TestCase("free")]
    [TestCase("all")]
    [TestCase("day")]
    [TestCase("next")]
    [TestCase("del")]
    [TestCase("count")]
    [TestCase("brand")]
    public void Should_recognize_as_stop_word(string stopWord)
    {
        var axes = new List<Axis>
        {
            new("Axis 0", new[]
            {
                new AxisValue(stopWord, new[] { new Ngram(stopWord, new[] { stopWord }, 30) }),
                new AxisValue("disc", new[] { new Ngram("disc", new[] { "disc" }, 30) }),
                new AxisValue("digital", new[] { new Ngram("digital", new[] { "digital" }, 30) }),
            }),
        };

        var result = TaxonomyService.PruneGenericValues(axes);

        var labels = result[0].Values.Select(v => v.Label).ToList();
        Assert.That(labels, Does.Not.Contain(stopWord));
    }
}
