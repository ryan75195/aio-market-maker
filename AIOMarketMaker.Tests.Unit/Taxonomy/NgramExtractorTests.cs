using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class NgramExtractorTests
{
    private NgramExtractor _extractor;

    [SetUp]
    public void SetUp()
    {
        _extractor = new NgramExtractor(null!);
    }

    [Test]
    public void Should_extract_unigrams_from_single_title()
    {
        var titles = Enumerable.Repeat("PlayStation Console Digital", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "playstation"), Is.True);
        Assert.That(result.Any(n => n.Term == "console"), Is.True);
        Assert.That(result.Any(n => n.Term == "digital"), Is.True);
    }

    [Test]
    public void Should_extract_bigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Console White", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "ps5 slim"), Is.True);
        Assert.That(result.Any(n => n.Term == "slim console"), Is.True);
    }

    [Test]
    public void Should_extract_trigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Digital Console", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "ps5 slim digital"), Is.True);
    }

    [Test]
    public void Should_filter_stop_words()
    {
        var titles = Enumerable.Repeat("the best new console for gaming", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "the"), Is.False);
        Assert.That(result.Any(n => n.Term == "for"), Is.False);
    }

    [Test]
    public void Should_filter_single_character_words()
    {
        var titles = Enumerable.Repeat("PS5 x controller", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "x"), Is.False);
    }

    [Test]
    public void Should_count_frequency_across_titles()
    {
        var titles = Enumerable.Repeat("PS5 Console", 50)
            .Concat(Enumerable.Repeat("Xbox Console", 30));
        var result = _extractor.Extract(titles).ToList();

        var console = result.First(n => n.Term == "console");
        Assert.That(console.Frequency, Is.EqualTo(80));
    }

    [Test]
    public void Should_scale_frequency_threshold_with_listing_count()
    {
        var titles = Enumerable.Repeat("PS5 Console", 4000);
        var rare = Enumerable.Repeat("RareWord Console", 19);
        var result = _extractor.Extract(titles.Concat(rare)).ToList();

        Assert.That(result.Any(n => n.Term == "rareword"), Is.False);
    }

    [Test]
    public void Should_return_lowercase_terms()
    {
        var titles = Enumerable.Repeat("PlayStation CONSOLE Digital", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.All(n => n.Term == n.Term.ToLowerInvariant()));
    }

    [Test]
    public void Should_identify_numeric_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("256gb", "512gb"), Is.True);
        Assert.That(NgramExtractor.AreNumericVariants("size 10", "size 11"), Is.True);
    }

    [Test]
    public void Should_not_flag_non_numeric_differences_as_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("red", "blue"), Is.False);
        Assert.That(NgramExtractor.AreNumericVariants("256gb", "256gb"), Is.False);
    }

    [Test]
    public void Should_not_flag_structurally_different_strings_as_variants()
    {
        Assert.That(NgramExtractor.AreNumericVariants("256gb ssd", "512"), Is.False);
    }

    [Test]
    public async Task Should_merge_synonyms_above_similarity_threshold()
    {
        var ngrams = new[]
        {
            new RawNgram("playstation 5", 100),
            new RawNgram("ps5", 200),
            new RawNgram("digital", 50),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },
                new[] { 0.98f, 0.1f, 0f },
                new[] { 0f, 0f, 1f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
        var merged = result.First(n => n.Canonical == "ps5");
        Assert.That(merged.Frequency, Is.EqualTo(300));
        Assert.That(merged.Forms, Does.Contain("ps5"));
        Assert.That(merged.Forms, Does.Contain("playstation 5"));
    }

    [Test]
    public async Task Should_not_merge_numeric_variants_even_when_similar()
    {
        var ngrams = new[]
        {
            new RawNgram("256gb", 100),
            new RawNgram("512gb", 80),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f },
                new[] { 0.99f, 0.01f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }

    // ======================================================================
    // SEARCH TERM FILTERING TESTS
    // ======================================================================

    [Test]
    public void Should_exclude_search_term_tokens_from_extraction()
    {
        // Search term is "PlayStation 5 Console" — those tokens should be filtered out
        var titles = Enumerable.Repeat("PlayStation 5 Console Digital Edition White", 25);
        var result = _extractor.Extract(titles, searchTerm: "PlayStation 5 Console").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Any(n => n.Term == "playstation"), Is.False,
                "Search term token 'playstation' should be excluded");
            Assert.That(result.Any(n => n.Term == "console"), Is.False,
                "Search term token 'console' should be excluded");
            Assert.That(result.Any(n => n.Term == "digital"), Is.True,
                "Non-search-term token 'digital' should still be extracted");
            Assert.That(result.Any(n => n.Term == "white"), Is.True,
                "Non-search-term token 'white' should still be extracted");
        });
    }

    [Test]
    public void Should_extract_all_ngrams_when_no_search_term_provided()
    {
        var titles = Enumerable.Repeat("PlayStation Console Digital", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Term == "playstation"), Is.True,
            "Without search term, all tokens should be extracted");
        Assert.That(result.Any(n => n.Term == "console"), Is.True);
    }

    [Test]
    public void Should_exclude_search_term_tokens_from_bigrams()
    {
        // "rolex submariner" search term — bigrams containing those tokens should be affected
        var titles = Enumerable.Repeat("Rolex Submariner Date Gold Dial", 25);
        var result = _extractor.Extract(titles, searchTerm: "Rolex Submariner").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Any(n => n.Term == "rolex"), Is.False);
            Assert.That(result.Any(n => n.Term == "submariner"), Is.False);
            // Bigrams formed from remaining tokens should still work
            Assert.That(result.Any(n => n.Term == "gold dial"), Is.True,
                "Bigram from non-search tokens should be extracted");
        });
    }

    // ======================================================================
    // BUG TESTS — discovered from backfill data analysis (2026-03-07)
    // ======================================================================

    // BUG 2: Plural and singular forms produce separate n-grams.
    // Real data: Dyson Airwrap has both "attachment" and "attachments" as
    //            separate Axis 0 values. Nike Air Jordan 1 has "sneakers"
    //            and "sneaker". These should normalize to the same form.
    [Test]
    [TestCase("attachment", "attachments")]
    [TestCase("accessory", "accessories")]
    [TestCase("controller", "controllers")]
    [TestCase("sneaker", "sneakers")]
    [TestCase("charger", "chargers")]
    public void Should_normalize_simple_plurals_to_singular(string singular, string plural)
    {
        // Mix of singular and plural forms — both should produce the same canonical
        var titles = Enumerable.Repeat($"PS5 {singular} white", 15)
            .Concat(Enumerable.Repeat($"PS5 {plural} white", 15));
        var result = _extractor.Extract(titles).ToList();

        var singularResult = result.FirstOrDefault(n => n.Term == singular);
        var pluralResult = result.FirstOrDefault(n => n.Term == plural);

        Assert.Multiple(() =>
        {
            // Should have one form, not both
            Assert.That(singularResult != null || pluralResult != null, Is.True,
                "At least one form should exist");
            Assert.That(singularResult != null && pluralResult != null, Is.False,
                $"Should not have both '{singular}' AND '{plural}' as separate n-grams");
        });
    }

    // BUG 3: Spelling variants like "disc"/"disk" and "grey"/"gray" escape
    //         the embedding dedup (0.95 threshold) and become separate axis values.
    // Real data: PlayStation 5 has both "disc" (1932 listings) and "disk" (227 listings)
    //            as separate Axis 0 values. These are the same variant.
    // This test verifies that the Deduplicate step merges spelling variants
    // even when embedding similarity is below 0.95 (which it often is for
    // single-word near-synonyms).
    [Test]
    [TestCase("disc", "disk")]
    [TestCase("grey", "gray")]
    [TestCase("colour", "color")]
    public async Task Should_merge_spelling_variants_via_edit_distance(string formA, string formB)
    {
        var ngrams = new[]
        {
            new RawNgram(formA, 200),
            new RawNgram(formB, 50),
            new RawNgram("console", 300),
        };

        // Simulate embeddings where disc/disk are similar but below 0.95 cosine
        // cosine(formA, formB) = 0.85 / (1.0 * sqrt(0.85^2 + 0.527^2)) = 0.85
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },         // formA
                new[] { 0.85f, 0.527f, 0f },  // formB — cosine ~0.85, below 0.95
                new[] { 0f, 0f, 1f },          // console — unrelated
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2),
            $"'{formA}' and '{formB}' should merge into one, leaving 2 total n-grams");
        var merged = result.First(n => n.Canonical == formA);
        Assert.That(merged.Forms, Does.Contain(formB),
            $"'{formB}' should be tracked as an alternative form of '{formA}'");
    }

    // Extract() is frequency-based only — it correctly emits both the short and
    // long n-gram. Redundancy is resolved later by SubsumeByTokenOverlap()
    // (see NgramExtractor_SubsumptionTests for those tests).
    [Test]
    [TestCase("ds218", "synology ds218")]
    [TestCase("ds223", "synology ds223")]
    public void Should_extract_both_unigram_and_containing_bigram(string unigram, string bigram)
    {
        var titles = Enumerable.Repeat($"Synology {unigram} NAS 2-Bay", 30)
            .ToList();
        var result = _extractor.Extract(titles).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Any(n => n.Term == unigram), Is.True,
                $"'{unigram}' should be extracted as a unigram");
            Assert.That(result.Any(n => n.Term == bigram), Is.True,
                $"'{bigram}' should be extracted as a bigram");
        });
    }

    // Extract() emits both bigram and trigram — SubsumeByTokenOverlap() resolves later.
    [Test]
    public void Should_extract_both_bigram_and_containing_trigram()
    {
        // "bay nas" is a bigram, "bay nas enclosure" is a trigram (contiguous tokens)
        var titles = Enumerable.Repeat("Synology 2-Bay NAS Enclosure Storage Server", 30)
            .ToList();
        var result = _extractor.Extract(titles).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(result.Any(n => n.Term == "bay nas"), Is.True,
                "'bay nas' should be extracted as a bigram");
            Assert.That(result.Any(n => n.Term == "bay nas enclosure"), Is.True,
                "'bay nas enclosure' should be extracted as a trigram");
        });
    }

    // ======================================================================
    // EDGE CASE / PRODUCTION-HARDENING TESTS
    // ======================================================================

    [Test]
    public void Should_return_empty_when_given_no_titles()
    {
        var result = _extractor.Extract(Enumerable.Empty<string>()).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_return_empty_when_all_titles_are_stopwords()
    {
        var titles = Enumerable.Repeat("the and or of for", 30);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_return_empty_when_titles_are_empty_strings()
    {
        var titles = Enumerable.Repeat("", 30);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_handle_single_word_titles()
    {
        var titles = Enumerable.Repeat("PlayStation", 25);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Term, Is.EqualTo("playstation"));
    }

    [Test]
    public void Should_handle_titles_with_only_single_character_words()
    {
        var titles = Enumerable.Repeat("x y z", 30);
        var result = _extractor.Extract(titles).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_handle_production_scale_title_count()
    {
        // Simulates a real job: 5000 titles with varied vocabulary
        var titles = new List<string>();
        for (var i = 0; i < 5000; i++)
        {
            titles.Add($"PlayStation 5 Disc Console White Bundle Controller {i % 50}");
        }

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Count, Is.GreaterThan(0));
        Assert.That(result.Any(n => n.Term == "playstation"), Is.True);
        Assert.That(result.Any(n => n.Term == "console"), Is.True);
    }

    [Test]
    public void Should_not_count_same_ngram_twice_in_one_title()
    {
        // "console" appears twice in this title but should only count once per title
        var titles = Enumerable.Repeat("console gaming console setup", 25);
        var result = _extractor.Extract(titles).ToList();

        var console = result.First(n => n.Term == "console");
        Assert.That(console.Frequency, Is.EqualTo(25));
    }

    [Test]
    public async Task Should_return_empty_when_merge_synonyms_given_empty_input()
    {
        var result = (await _extractor.MergeSynonyms(Enumerable.Empty<RawNgram>())).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task Should_return_single_ngram_unchanged_when_only_one_input()
    {
        var ngrams = new[] { new RawNgram("console", 100) };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[] { new[] { 1f, 0f } });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Canonical, Is.EqualTo("console"));
        Assert.That(result[0].Frequency, Is.EqualTo(100));
    }

    [Test]
    public async Task Should_preserve_all_ngrams_when_none_are_similar()
    {
        var ngrams = new[]
        {
            new RawNgram("red", 100),
            new RawNgram("console", 200),
            new RawNgram("digital", 150),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },
                new[] { 0f, 1f, 0f },
                new[] { 0f, 0f, 1f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task Should_pick_highest_frequency_as_canonical_when_merging()
    {
        var ngrams = new[]
        {
            new RawNgram("playstation 5", 50),
            new RawNgram("ps5", 300),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f },
                new[] { 0.99f, 0.01f },
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Canonical, Is.EqualTo("ps5"),
            "Highest frequency term should become the canonical");
        Assert.That(result[0].Frequency, Is.EqualTo(350));
    }

    [Test]
    public async Task Should_handle_transitive_merges()
    {
        // A similar to B, B similar to C → all three should merge
        var ngrams = new[]
        {
            new RawNgram("form a", 100),
            new RawNgram("form b", 200),
            new RawNgram("form c", 50),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>(), It.IsAny<EmbeddingModel>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },
                new[] { 0.98f, 0.1f, 0f },     // similar to A
                new[] { 0.96f, 0.15f, 0.01f },  // similar to B (and transitively A)
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.MergeSynonyms(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(1),
            "All three forms should merge transitively into one");
        Assert.That(result[0].Canonical, Is.EqualTo("form b"),
            "Highest frequency should be canonical");
        Assert.That(result[0].Forms.Count(), Is.EqualTo(3));
    }
}
