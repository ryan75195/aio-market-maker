using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.Extensions.Logging;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TitleDecontaminator_UnitTests
{
    private Mock<IEmbeddingService> _embeddingMock = null!;
    private TitleDecontaminator _decontaminator = null!;

    [SetUp]
    public void SetUp()
    {
        _embeddingMock = new Mock<IEmbeddingService>();
        _decontaminator = new TitleDecontaminator(
            _embeddingMock.Object,
            Mock.Of<ILogger<TitleDecontaminator>>());
    }

    // --- Brand token check ---

    [Test]
    public async Task Should_remove_title_missing_all_brand_tokens()
    {
        var titles = new[] { "Rolex Submariner Black", "Tudor Black Bay 58" };
        var brandTokens = new[] { "rolex" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(1));
        Assert.That(result.FilteredTitles.First(), Is.EqualTo("Rolex Submariner Black"));
        Assert.That(result.ExcludedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Should_keep_title_containing_brand_token()
    {
        var titles = new[] { "Rolex Submariner 116610LN" };
        var brandTokens = new[] { "rolex" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(1));
        Assert.That(result.ExcludedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_match_brand_tokens_case_insensitively()
    {
        var titles = new[] { "ROLEX SUBMARINER DATE" };
        var brandTokens = new[] { "rolex" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Should_skip_filtering_when_brand_tokens_null()
    {
        var titles = new[] { "Title A", "Title B", "Title C" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Product Name", null);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(3));
        Assert.That(result.ExcludedCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_skip_filtering_when_brand_tokens_empty()
    {
        var titles = new[] { "Title A", "Title B" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Product Name", Array.Empty<string>());

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(2));
        Assert.That(result.ExcludedCount, Is.EqualTo(0));
    }

    // --- Embedding similarity check ---

    [Test]
    public async Task Should_remove_title_with_low_embedding_similarity()
    {
        var titles = new[] { "Rolex Submariner 116610", "Rolex Submariner Bezel Insert" };
        var brandTokens = new[] { "rolex" };

        var productEmb = new float[] { 1f, 0f, 0f };
        var titleEmbs = new float[][]
        {
            new float[] { 0.95f, 0.1f, 0f },  // high similarity
            new float[] { 0.3f, 0.8f, 0.5f },  // low similarity
        };

        _embeddingMock
            .Setup(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(productEmb);
        _embeddingMock
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(titleEmbs);

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(1));
        Assert.That(result.FilteredTitles.First(), Does.Contain("116610"));
        Assert.That(result.ExcludedCount, Is.EqualTo(1));
    }

    // --- Index remapping ---

    [Test]
    public async Task Should_provide_correct_index_mapping()
    {
        var titles = new[] { "Rolex Submariner Black", "Tudor Black Bay", "Rolex Submariner Blue" };
        var brandTokens = new[] { "rolex" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.Multiple(() =>
        {
            Assert.That(result.FilteredTitles.Count(), Is.EqualTo(2));
            Assert.That(result.FilteredToOriginalIndex[0], Is.EqualTo(0));
            Assert.That(result.FilteredToOriginalIndex[1], Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Should_return_identity_mapping_when_no_filtering()
    {
        var titles = new[] { "Rolex Sub A", "Rolex Sub B" };
        var brandTokens = new[] { "rolex" };
        SetupHighSimilarityEmbeddings();

        var result = await _decontaminator.Filter(titles, "Rolex Submariner", brandTokens);

        Assert.Multiple(() =>
        {
            Assert.That(result.FilteredToOriginalIndex[0], Is.EqualTo(0));
            Assert.That(result.FilteredToOriginalIndex[1], Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Should_skip_embedding_check_when_product_name_null()
    {
        var titles = new[] { "Rolex Submariner", "Tudor Black Bay" };
        var brandTokens = new[] { "rolex" };

        var result = await _decontaminator.Filter(titles, null, brandTokens);

        Assert.That(result.FilteredTitles.Count(), Is.EqualTo(1));
        Assert.That(result.FilteredTitles.First(), Does.Contain("Rolex"));
    }

    // --- Helpers ---

    private void SetupHighSimilarityEmbeddings()
    {
        var highSimVector = new float[] { 1f, 0f, 0f };

        _embeddingMock
            .Setup(e => e.GetEmbedding(It.IsAny<string>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync(highSimVector);
        _embeddingMock
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), EmbeddingModel.Small))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _, EmbeddingModel __) =>
                texts.Select(_ => highSimVector).ToArray());
    }
}
