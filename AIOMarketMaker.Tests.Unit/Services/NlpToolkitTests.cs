using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class NlpToolkitTests
{
    private NlpToolkit _toolkit = null!;

    [SetUp]
    public void SetUp()
    {
        _toolkit = new NlpToolkit();
    }

    // --- Singularize ---

    [Test]
    [TestCase("controllers", "controller")]
    [TestCase("chargers", "charger")]
    [TestCase("sneakers", "sneaker")]
    [TestCase("attachments", "attachment")]
    [TestCase("drives", "drive")]
    [TestCase("printers", "printer")]
    [TestCase("toners", "toner")]
    [TestCase("pages", "page")]
    public void Should_strip_simple_plural_suffix(string plural, string expected)
    {
        Assert.That(_toolkit.Singularize(plural), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("accessories", "accessory")]
    [TestCase("batteries", "battery")]
    [TestCase("warranties", "warranty")]
    public void Should_normalize_ies_to_y(string plural, string expected)
    {
        Assert.That(_toolkit.Singularize(plural), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("shelves", "shelf")]
    [TestCase("halves", "half")]
    public void Should_normalize_ves_to_f(string plural, string expected)
    {
        Assert.That(_toolkit.Singularize(plural), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("cases", "case")]
    [TestCase("ouses", "ouse")]
    public void Should_normalize_ses_by_stripping_s(string plural, string expected)
    {
        Assert.That(_toolkit.Singularize(plural), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("wireless", "wireless")]
    [TestCase("stainless", "stainless")]
    public void Should_not_strip_ss_endings(string word, string expected)
    {
        Assert.That(_toolkit.Singularize(word), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("status", "status")]
    [TestCase("nexus", "nexus")]
    public void Should_not_strip_us_endings(string word, string expected)
    {
        Assert.That(_toolkit.Singularize(word), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("nas", "nas")]
    [TestCase("gas", "gas")]
    [TestCase("ssd", "ssd")]
    [TestCase("ps5", "ps5")]
    public void Should_not_modify_short_words(string word, string expected)
    {
        Assert.That(_toolkit.Singularize(word), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("disc", "disc")]
    [TestCase("digital", "digital")]
    [TestCase("controller", "controller")]
    public void Should_return_singular_words_unchanged(string word, string expected)
    {
        Assert.That(_toolkit.Singularize(word), Is.EqualTo(expected));
    }

    [Test]
    [TestCase("series", "series")]
    [TestCase("lens", "lens")]
    public void Should_handle_known_exceptions(string word, string expected)
    {
        Assert.That(_toolkit.Singularize(word), Is.EqualTo(expected));
    }
}
