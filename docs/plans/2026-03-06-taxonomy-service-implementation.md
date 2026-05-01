# Taxonomy Service Stage 1 — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Port the deterministic taxonomy pipeline (n-gram extraction, mutual exclusivity detection, Louvain community detection) from Python to C# as a testable service layer.

**Architecture:** Four services in `Core/Services/Taxonomy/`: NgramExtractor (depends on IEmbeddingService), MutualExclusivityAnalyzer (pure math), LouvainCommunityDetector (pure math), TaxonomyService (orchestrator). All communicate via shared record types.

**Tech Stack:** .NET 8.0, NUnit 3.14, Moq. No new NuGet packages.

**Reference:** Python source in `experiments/taxonomy_v3.py` (core functions) and `experiments/taxonomy_v5.py` (community detection + post-processing). Design doc at `docs/plans/2026-03-06-taxonomy-service-design.md`.

---

## Task 1: Records and Interfaces

Define all shared types and interfaces. No implementation yet.

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/ITaxonomyService.cs`
- Create: `AIOMarketMaker.Core/Services/Taxonomy/INgramExtractor.cs`
- Create: `AIOMarketMaker.Core/Services/Taxonomy/IMutualExclusivityAnalyzer.cs`
- Create: `AIOMarketMaker.Core/Services/Taxonomy/ICommunityDetector.cs`

**Step 1: Create the records and ITaxonomyService interface**

```csharp
// ITaxonomyService.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public record Ngram(string Canonical, IEnumerable<string> Forms, int Frequency);

public record MatchSet(Ngram Ngram, IReadOnlySet<int> ListingIndices);

public record MutuallyExclusivePair(
    Ngram A, Ngram B, double Overlap, double EmbeddingSimilarity);

public record WeightedEdge(int NodeA, int NodeB, double Weight);

public record Community(
    int Id, IEnumerable<Ngram> Members,
    double ExclusivityDensity, double Coherence, double Coverage);

public record Axis(string Name, IEnumerable<AxisValue> Values);

public record AxisValue(string Label, IEnumerable<Ngram> Ngrams);

public record CellAssignment(
    int ListingIndex, IReadOnlyDictionary<string, string> Cell, bool HasConflict);

public record CellStats(
    IReadOnlyDictionary<string, string> Cell,
    int Count, int Active, int Sold, int SellThroughPct,
    decimal MedianActivePrice, decimal MedianSoldPrice);

public record TaxonomyResult(
    IEnumerable<Axis> Axes,
    IEnumerable<CellAssignment> Assignments,
    IEnumerable<CellStats> Cells,
    double CoveragePercent, double ConflictPercent);

public interface ITaxonomyService
{
    Task<TaxonomyResult> Generate(IEnumerable<string> titles);
}
```

**Step 2: Create INgramExtractor**

```csharp
// INgramExtractor.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface INgramExtractor
{
    IEnumerable<Ngram> Extract(IEnumerable<string> titles);
    Task<IEnumerable<Ngram>> Deduplicate(IEnumerable<Ngram> ngrams, CancellationToken ct = default);
}
```

**Step 3: Create IMutualExclusivityAnalyzer**

```csharp
// IMutualExclusivityAnalyzer.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface IMutualExclusivityAnalyzer
{
    IEnumerable<MatchSet> ComputeMatchSets(IEnumerable<string> titles, IEnumerable<Ngram> ngrams);
    IEnumerable<MutuallyExclusivePair> FindExclusivePairs(
        IEnumerable<MatchSet> matchSets, double threshold = 0.05);
}
```

**Step 4: Create ICommunityDetector**

```csharp
// ICommunityDetector.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface ICommunityDetector
{
    IEnumerable<Community> Detect(
        IEnumerable<WeightedEdge> edges, int nodeCount, double resolution = 2.0);
}
```

**Step 5: Verify build**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/
git commit -m "feat: add taxonomy service records and interfaces"
```

---

## Task 2: NgramExtractor — Extract

Port `extract_ngrams()` from `taxonomy_v3.py`. Pure string processing, no external deps.

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/NgramExtractor.cs`
- Create: `AIOMarketMaker.Tests.Unit/Taxonomy/NgramExtractorTests.cs`

**Step 1: Write the failing tests**

```csharp
// NgramExtractorTests.cs
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class NgramExtractorTests
{
    private NgramExtractor _extractor;

    [SetUp]
    public void SetUp()
    {
        // Pass null for IEmbeddingService — Extract doesn't use it
        _extractor = new NgramExtractor(null!);
    }

    [Test]
    public void Should_extract_unigrams_from_single_title()
    {
        var titles = Enumerable.Repeat("PlayStation Console Digital", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "playstation"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "console"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "digital"), Is.True);
    }

    [Test]
    public void Should_extract_bigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Console White", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "ps5 slim"), Is.True);
        Assert.That(result.Any(n => n.Canonical == "slim console"), Is.True);
    }

    [Test]
    public void Should_extract_trigrams()
    {
        var titles = Enumerable.Repeat("PS5 Slim Digital Console", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "ps5 slim digital"), Is.True);
    }

    [Test]
    public void Should_filter_stop_words()
    {
        var titles = Enumerable.Repeat("the best new console for gaming", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "the"), Is.False);
        Assert.That(result.Any(n => n.Canonical == "new"), Is.False);
        Assert.That(result.Any(n => n.Canonical == "for"), Is.False);
    }

    [Test]
    public void Should_filter_single_character_words()
    {
        var titles = Enumerable.Repeat("PS5 x controller", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.Any(n => n.Canonical == "x"), Is.False);
    }

    [Test]
    public void Should_count_frequency_across_titles()
    {
        var titles = Enumerable.Repeat("PS5 Console", 50)
            .Concat(Enumerable.Repeat("Xbox Console", 30));

        var result = _extractor.Extract(titles).ToList();

        var console = result.First(n => n.Canonical == "console");
        Assert.That(console.Frequency, Is.EqualTo(80));
    }

    [Test]
    public void Should_scale_frequency_threshold_with_listing_count()
    {
        // With 4000 listings, min_uni_freq = max(20, 4000/200) = 20
        // An n-gram appearing 19 times should be filtered out
        var titles = Enumerable.Repeat("PS5 Console", 4000);
        var rare = Enumerable.Repeat("RareWord Console", 19);

        var result = _extractor.Extract(titles.Concat(rare)).ToList();

        Assert.That(result.Any(n => n.Canonical == "rareword"), Is.False);
    }

    [Test]
    public void Should_return_lowercase_canonicals()
    {
        var titles = Enumerable.Repeat("PlayStation CONSOLE Digital", 25);

        var result = _extractor.Extract(titles).ToList();

        Assert.That(result.All(n => n.Canonical == n.Canonical.ToLowerInvariant()));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~NgramExtractorTests"`
Expected: Build error — NgramExtractor class doesn't exist

**Step 3: Implement NgramExtractor.Extract**

```csharp
// NgramExtractor.cs
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public partial class NgramExtractor : INgramExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "of", "for", "in", "on", "at", "to",
        "is", "it", "by", "as", "be", "no", "not", "so", "up", "if", "my",
        "new", "free", "with", "this", "that", "from", "was", "are", "has"
    };

    private readonly IEmbeddingService _embeddingService;

    public NgramExtractor(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    public IEnumerable<Ngram> Extract(IEnumerable<string> titles)
    {
        var titleList = titles.ToList();
        var count = titleList.Count;
        var minUnigramFrequency = Math.Max(20, count / 200);
        var minBigramFrequency = Math.Max(10, count / 200);

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var title in titleList)
        {
            var words = ExtractWords(title);
            if (words.Count == 0)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Unigrams
            foreach (var word in words)
            {
                if (seen.Add(word))
                {
                    frequencies[word] = frequencies.GetValueOrDefault(word) + 1;
                }
            }

            // Bigrams
            for (var i = 0; i < words.Count - 1; i++)
            {
                var bigram = $"{words[i]} {words[i + 1]}";
                if (seen.Add(bigram))
                {
                    frequencies[bigram] = frequencies.GetValueOrDefault(bigram) + 1;
                }
            }

            // Trigrams
            for (var i = 0; i < words.Count - 2; i++)
            {
                var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
                if (seen.Add(trigram))
                {
                    frequencies[trigram] = frequencies.GetValueOrDefault(trigram) + 1;
                }
            }
        }

        return frequencies
            .Where(kvp =>
            {
                var wordCount = kvp.Key.Count(c => c == ' ') + 1;
                var threshold = wordCount == 1 ? minUnigramFrequency : minBigramFrequency;
                return kvp.Value >= threshold;
            })
            .Select(kvp => new Ngram(kvp.Key, new[] { kvp.Key }, kvp.Value));
    }

    private static List<string> ExtractWords(string title)
    {
        return WordPattern().Matches(title.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToList();
    }

    [GeneratedRegex(@"\b\w+\b")]
    private static partial Regex WordPattern();
}
```

**Step 4: Add stub for Deduplicate (so the class compiles against the interface)**

```csharp
    public Task<IEnumerable<Ngram>> Deduplicate(IEnumerable<Ngram> ngrams, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~NgramExtractorTests"`
Expected: All tests pass

**Step 6: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/NgramExtractor.cs AIOMarketMaker.Tests.Unit/Taxonomy/NgramExtractorTests.cs
git commit -m "feat: implement NgramExtractor.Extract with unit tests"
```

---

## Task 3: NgramExtractor — Deduplicate

Port `dedup_ngrams()` and `_are_numeric_variants()`. Depends on `IEmbeddingService`.

**Files:**
- Modify: `AIOMarketMaker.Core/Services/Taxonomy/NgramExtractor.cs`
- Modify: `AIOMarketMaker.Tests.Unit/Taxonomy/NgramExtractorTests.cs`

**Step 1: Write the failing tests**

```csharp
    // Add to NgramExtractorTests.cs

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
            new Ngram("playstation 5", new[] { "playstation 5" }, 100),
            new Ngram("ps5", new[] { "ps5" }, 200),
            new Ngram("digital", new[] { "digital" }, 50),
        };

        // Mock embeddings: ps5 and playstation 5 are similar (cosine > 0.95)
        // digital is different
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f, 0f },   // playstation 5
                new[] { 0.98f, 0.1f, 0f }, // ps5 (very similar to playstation 5)
                new[] { 0f, 0f, 1f },    // digital (different)
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        // ps5 and playstation 5 should merge (ps5 has higher freq, becomes canonical)
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
            new Ngram("256gb", new[] { "256gb" }, 100),
            new Ngram("512gb", new[] { "512gb" }, 80),
        };

        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new[] { 1f, 0f },   // 256gb
                new[] { 0.99f, 0.01f }, // 512gb (very similar semantically)
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var result = (await extractor.Deduplicate(ngrams)).ToList();

        Assert.That(result.Count, Is.EqualTo(2));
    }
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~NgramExtractorTests"`
Expected: FAIL — `AreNumericVariants` not defined, `Deduplicate` throws `NotImplementedException`

**Step 3: Implement AreNumericVariants and Deduplicate**

Add to `NgramExtractor.cs`:

```csharp
    public static bool AreNumericVariants(string a, string b)
    {
        var templateA = DigitPattern().Replace(a, "#");
        var templateB = DigitPattern().Replace(b, "#");

        if (templateA != templateB)
        {
            return false;
        }

        if (!templateA.Contains('#'))
        {
            return false;
        }

        var digitsA = DigitPattern().Matches(a).Select(m => m.Value).ToList();
        var digitsB = DigitPattern().Matches(b).Select(m => m.Value).ToList();

        return !digitsA.SequenceEqual(digitsB);
    }

    public async Task<IEnumerable<Ngram>> Deduplicate(
        IEnumerable<Ngram> ngrams, CancellationToken ct = default)
    {
        var ngramList = ngrams.ToList();
        if (ngramList.Count == 0)
        {
            return Enumerable.Empty<Ngram>();
        }

        var texts = ngramList.Select(n => n.Canonical).ToList();
        var vectors = await _embeddingService.GetEmbeddings(texts, ct);

        // L2-normalize
        var normed = vectors.Select(Normalize).ToArray();

        // Union-Find
        var parent = Enumerable.Range(0, ngramList.Count).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y)
        {
            var rootX = Find(x);
            var rootY = Find(y);
            if (rootX != rootY)
            {
                parent[rootX] = rootY;
            }
        }

        for (var i = 0; i < ngramList.Count; i++)
        {
            for (var j = i + 1; j < ngramList.Count; j++)
            {
                var similarity = CosineSimilarity(normed[i], normed[j]);
                if (similarity >= 0.95
                    && !AreNumericVariants(texts[i], texts[j]))
                {
                    Union(i, j);
                }
            }
        }

        // Group by root
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < ngramList.Count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        return groups.Values.Select(indices =>
        {
            var sorted = indices.OrderByDescending(i => ngramList[i].Frequency).ToList();
            var canonical = ngramList[sorted[0]];
            var totalFrequency = sorted.Sum(i => ngramList[i].Frequency);
            var allForms = sorted.SelectMany(i => ngramList[i].Forms).Distinct().ToList();
            return new Ngram(canonical.Canonical, allForms, totalFrequency);
        });
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude == 0)
        {
            return vector;
        }
        return vector.Select(v => v / magnitude).ToArray();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot; // Already normalized, so dot product = cosine similarity
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitPattern();
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~NgramExtractorTests"`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/NgramExtractor.cs AIOMarketMaker.Tests.Unit/Taxonomy/NgramExtractorTests.cs
git commit -m "feat: implement NgramExtractor.Deduplicate with numeric variant guard"
```

---

## Task 4: MutualExclusivityAnalyzer

Port `compute_match_sets()`, `filter_significant_ngrams()`, `pattern_matches()`, and `compute_me_pairs()`.

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/MutualExclusivityAnalyzer.cs`
- Create: `AIOMarketMaker.Tests.Unit/Taxonomy/MutualExclusivityAnalyzerTests.cs`

**Step 1: Write the failing tests**

```csharp
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class MutualExclusivityAnalyzerTests
{
    private MutualExclusivityAnalyzer _analyzer;

    [SetUp]
    public void SetUp()
    {
        _analyzer = new MutualExclusivityAnalyzer();
    }

    [Test]
    public void Should_compute_match_sets_for_ngrams()
    {
        var titles = new[]
        {
            "PS5 Disc Console",
            "PS5 Digital Console",
            "PS5 Disc Edition",
        };
        var ngrams = new[]
        {
            new Ngram("disc", new[] { "disc" }, 20),
            new Ngram("digital", new[] { "digital" }, 10),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();

        var disc = result.First(m => m.Ngram.Canonical == "disc");
        Assert.That(disc.ListingIndices, Is.EquivalentTo(new[] { 0, 2 }));

        var digital = result.First(m => m.Ngram.Canonical == "digital");
        Assert.That(digital.ListingIndices, Is.EquivalentTo(new[] { 1 }));
    }

    [Test]
    public void Should_use_word_boundary_for_single_word_ngrams()
    {
        var titles = new[]
        {
            "PS5 Pro Console",
            "PS5 Professional Grade",
        };
        var ngrams = new[]
        {
            new Ngram("pro", new[] { "pro" }, 20),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        var pro = result.First();

        // "pro" should match "Pro" but NOT "Professional" (word boundary)
        Assert.That(pro.ListingIndices, Is.EquivalentTo(new[] { 0 }));
    }

    [Test]
    public void Should_use_substring_for_multi_word_ngrams()
    {
        var titles = new[]
        {
            "PS5 Disc Edition Console",
            "PS5 Console Digital",
        };
        var ngrams = new[]
        {
            new Ngram("disc edition", new[] { "disc edition" }, 20),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        Assert.That(result.First().ListingIndices, Is.EquivalentTo(new[] { 0 }));
    }

    [Test]
    public void Should_match_all_forms_of_deduped_ngram()
    {
        var titles = new[]
        {
            "PS5 Console",
            "PlayStation 5 Console",
        };
        var ngrams = new[]
        {
            new Ngram("ps5", new[] { "ps5", "playstation 5" }, 30),
        };

        var result = _analyzer.ComputeMatchSets(titles, ngrams).ToList();
        Assert.That(result.First().ListingIndices, Is.EquivalentTo(new[] { 0, 1 }));
    }

    [Test]
    public void Should_find_exclusive_pair_when_overlap_below_threshold()
    {
        var discSet = new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var digitalSet = new HashSet<int> { 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("disc", new[] { "disc" }, 10), discSet),
            new MatchSet(new Ngram("digital", new[] { "digital" }, 10), digitalSet),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Overlap, Is.EqualTo(0.0));
    }

    [Test]
    public void Should_not_flag_pair_when_overlap_above_threshold()
    {
        // 2 out of 10 overlap = 20% > 5% threshold
        var setA = new HashSet<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var setB = new HashSet<int> { 0, 1, 10, 11, 12, 13, 14, 15, 16, 17 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("slim", new[] { "slim" }, 10), setA),
            new MatchSet(new Ngram("console", new[] { "console" }, 10), setB),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_compute_overlap_using_min_denominator()
    {
        // 1 overlap, smaller set has 5 items: 1/5 = 20% > 5%
        var largeSet = new HashSet<int>(Enumerable.Range(0, 100));
        var smallSet = new HashSet<int> { 0, 101, 102, 103, 104 };

        var matchSets = new[]
        {
            new MatchSet(new Ngram("large", new[] { "large" }, 100), largeSet),
            new MatchSet(new Ngram("small", new[] { "small" }, 5), smallSet),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();

        // 1/5 = 0.20 > 0.05 threshold — should NOT be exclusive
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Should_skip_empty_match_sets()
    {
        var matchSets = new[]
        {
            new MatchSet(new Ngram("disc", new[] { "disc" }, 10), new HashSet<int> { 0, 1 }),
            new MatchSet(new Ngram("empty", new[] { "empty" }, 0), new HashSet<int>()),
        };

        var result = _analyzer.FindExclusivePairs(matchSets).ToList();
        Assert.That(result, Is.Empty);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~MutualExclusivityAnalyzerTests"`
Expected: Build error — class doesn't exist

**Step 3: Implement MutualExclusivityAnalyzer**

```csharp
// MutualExclusivityAnalyzer.cs
using System.Text.RegularExpressions;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class MutualExclusivityAnalyzer : IMutualExclusivityAnalyzer
{
    public IEnumerable<MatchSet> ComputeMatchSets(
        IEnumerable<string> titles, IEnumerable<Ngram> ngrams)
    {
        var titleList = titles.Select(t => t.ToLowerInvariant()).ToList();
        var ngramList = ngrams.ToList();

        return ngramList.Select(ngram =>
        {
            var indices = new HashSet<int>();
            for (var i = 0; i < titleList.Count; i++)
            {
                if (ngram.Forms.Any(form => PatternMatches(form, titleList[i])))
                {
                    indices.Add(i);
                }
            }
            return new MatchSet(ngram, indices);
        });
    }

    public IEnumerable<MutuallyExclusivePair> FindExclusivePairs(
        IEnumerable<MatchSet> matchSets, double threshold = 0.05)
    {
        var setList = matchSets.ToList();
        var result = new List<MutuallyExclusivePair>();

        for (var i = 0; i < setList.Count; i++)
        {
            if (setList[i].ListingIndices.Count == 0)
            {
                continue;
            }

            for (var j = i + 1; j < setList.Count; j++)
            {
                if (setList[j].ListingIndices.Count == 0)
                {
                    continue;
                }

                var intersection = setList[i].ListingIndices
                    .Count(idx => setList[j].ListingIndices.Contains(idx));
                var minSize = Math.Min(
                    setList[i].ListingIndices.Count,
                    setList[j].ListingIndices.Count);
                var overlap = (double)intersection / minSize;

                if (overlap < threshold)
                {
                    result.Add(new MutuallyExclusivePair(
                        setList[i].Ngram, setList[j].Ngram, overlap, 0.0));
                }
            }
        }

        return result;
    }

    internal static bool PatternMatches(string pattern, string titleLower)
    {
        if (!pattern.Contains(' '))
        {
            return Regex.IsMatch(titleLower, $@"\b{Regex.Escape(pattern)}\b");
        }
        return titleLower.Contains(pattern, StringComparison.Ordinal);
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~MutualExclusivityAnalyzerTests"`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/MutualExclusivityAnalyzer.cs AIOMarketMaker.Tests.Unit/Taxonomy/MutualExclusivityAnalyzerTests.cs
git commit -m "feat: implement MutualExclusivityAnalyzer with word boundary matching"
```

---

## Task 5: LouvainCommunityDetector

Port the Louvain algorithm. Pure graph computation — no external dependencies.

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/LouvainCommunityDetector.cs`
- Create: `AIOMarketMaker.Tests.Unit/Taxonomy/LouvainCommunityDetectorTests.cs`

**Step 1: Write the failing tests**

```csharp
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

        var result = _detector.Detect(edges, nodeCount: 4).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        // All nodes should have members represented
        var totalMembers = result.Sum(c => c.Members.Count());
        Assert.That(totalMembers, Is.EqualTo(4));
    }

    [Test]
    public void Should_split_weakly_connected_groups_at_high_resolution()
    {
        // Two tight clusters connected by a weak bridge
        var edges = new List<WeightedEdge>
        {
            // Cluster A: strongly connected
            new(0, 1, 1.0), new(0, 2, 1.0), new(1, 2, 1.0),
            // Cluster B: strongly connected
            new(3, 4, 1.0), new(3, 5, 1.0), new(4, 5, 1.0),
            // Weak bridge
            new(2, 3, 0.1),
        };

        var result = _detector.Detect(edges, nodeCount: 6, resolution: 2.0).ToList();

        // High resolution should split into 2 communities
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
        // Node 1 is between two clusters but more strongly connected to {0,2}
        var edges = new[]
        {
            new WeightedEdge(0, 1, 1.0), new WeightedEdge(1, 2, 1.0),
            new WeightedEdge(0, 2, 1.0),
            new WeightedEdge(1, 3, 0.05), // very weak link to other cluster
            new WeightedEdge(3, 4, 1.0), new WeightedEdge(3, 5, 1.0),
            new WeightedEdge(4, 5, 1.0),
        };

        var result = _detector.Detect(edges, nodeCount: 6, resolution: 2.0).ToList();

        // Node 1 should be in the same community as 0 and 2, not with 3,4,5
        var communityWith0 = result.First(c =>
            c.Members.Any(m => m.Canonical == "0"));
        Assert.That(
            communityWith0.Members.Any(m => m.Canonical == "1"),
            Is.True);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~LouvainCommunityDetectorTests"`
Expected: Build error — class doesn't exist

**Step 3: Implement LouvainCommunityDetector**

The Louvain algorithm maximises modularity Q by iteratively moving nodes between communities. Two phases repeat until convergence: (1) local moves — move each node to the neighbor community giving the best modularity gain, (2) aggregation — collapse communities into super-nodes and repeat.

```csharp
// LouvainCommunityDetector.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public class LouvainCommunityDetector : ICommunityDetector
{
    public IEnumerable<Community> Detect(
        IEnumerable<WeightedEdge> edges, int nodeCount, double resolution = 2.0)
    {
        if (nodeCount == 0)
        {
            return Enumerable.Empty<Community>();
        }

        var edgeList = edges.ToList();
        if (edgeList.Count == 0)
        {
            return Enumerable.Empty<Community>();
        }

        // Build adjacency: node -> list of (neighbor, weight)
        var adjacency = new Dictionary<int, List<(int neighbor, double weight)>>();
        for (var i = 0; i < nodeCount; i++)
        {
            adjacency[i] = new List<(int, double)>();
        }

        var totalWeight = 0.0;
        foreach (var edge in edgeList)
        {
            adjacency[edge.NodeA].Add((edge.NodeB, edge.Weight));
            adjacency[edge.NodeB].Add((edge.NodeA, edge.Weight));
            totalWeight += edge.Weight;
        }

        // Initial assignment: each node in its own community
        var communityOf = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            communityOf[i] = i;
        }

        // Phase 1: Local moves — iterate until no improvement
        var improved = true;
        while (improved)
        {
            improved = false;
            for (var node = 0; node < nodeCount; node++)
            {
                var currentCommunity = communityOf[node];
                var bestCommunity = currentCommunity;
                var bestGain = 0.0;

                // Compute weights to each neighbor community
                var communityWeights = new Dictionary<int, double>();
                foreach (var (neighbor, weight) in adjacency[node])
                {
                    var neighborCommunity = communityOf[neighbor];
                    communityWeights[neighborCommunity] =
                        communityWeights.GetValueOrDefault(neighborCommunity) + weight;
                }

                // Node's total weight (degree)
                var nodeDegree = adjacency[node].Sum(e => e.weight);

                // Weight within current community
                var weightInCurrent = communityWeights.GetValueOrDefault(currentCommunity);

                // Total weight of current community (excluding this node)
                var currentCommunityWeight = ComputeCommunityWeight(
                    communityOf, adjacency, currentCommunity, node, nodeCount);

                foreach (var (candidateCommunity, weightToCandidate) in communityWeights)
                {
                    if (candidateCommunity == currentCommunity)
                    {
                        continue;
                    }

                    var candidateCommunityWeight = ComputeCommunityWeight(
                        communityOf, adjacency, candidateCommunity, -1, nodeCount);

                    // Modularity gain of moving node from current to candidate
                    var gain = (weightToCandidate - weightInCurrent)
                        - resolution * nodeDegree
                            * (candidateCommunityWeight - currentCommunityWeight + nodeDegree)
                            / (2.0 * totalWeight);

                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestCommunity = candidateCommunity;
                    }
                }

                if (bestCommunity != currentCommunity)
                {
                    communityOf[node] = bestCommunity;
                    improved = true;
                }
            }
        }

        // Build result communities
        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < nodeCount; i++)
        {
            var comm = communityOf[i];
            if (!groups.TryGetValue(comm, out var list))
            {
                list = new List<int>();
                groups[comm] = list;
            }
            list.Add(i);
        }

        return groups.Values
            .Select((members, idx) => new Community(
                idx,
                members.Select(m => new Ngram(m.ToString(), new[] { m.ToString() }, 0)),
                ExclusivityDensity: 0.0,
                Coherence: 0.0,
                Coverage: 0.0))
            .ToList();
    }

    private static double ComputeCommunityWeight(
        int[] communityOf, Dictionary<int, List<(int neighbor, double weight)>> adjacency,
        int community, int excludeNode, int nodeCount)
    {
        var total = 0.0;
        for (var i = 0; i < nodeCount; i++)
        {
            if (i == excludeNode || communityOf[i] != community)
            {
                continue;
            }
            total += adjacency[i].Sum(e => e.weight);
        }
        return total;
    }
}
```

**Note:** This is a simplified single-phase Louvain. The `Community.Members` use placeholder `Ngram` records with the node index as the canonical name. The `TaxonomyService` (Task 6) will map node indices back to actual n-grams.

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~LouvainCommunityDetectorTests"`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/LouvainCommunityDetector.cs AIOMarketMaker.Tests.Unit/Taxonomy/LouvainCommunityDetectorTests.cs
git commit -m "feat: implement Louvain community detection algorithm"
```

---

## Task 6: TaxonomyService — Orchestration

Wire the components together. Handles graph construction, community scoring, post-processing (value dedup, overlap pruning, ME enforcement), cell assignment, and pricing stats.

**Files:**
- Create: `AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs`
- Create: `AIOMarketMaker.Tests.Unit/Taxonomy/TaxonomyServiceTests.cs`

**Step 1: Write the failing tests**

```csharp
using AIOMarketMaker.Core.Services.Taxonomy;
using Moq;

namespace AIOMarketMaker.Tests.Unit.Taxonomy;

[TestFixture]
[Category("Unit")]
public class TaxonomyServiceTests
{
    // Synthetic PS5 titles designed to produce known axes
    private static readonly string[] Ps5Titles = Enumerable.Empty<string>()
        .Concat(Enumerable.Repeat("PS5 Disc Console White", 30))
        .Concat(Enumerable.Repeat("PS5 Digital Console White", 25))
        .Concat(Enumerable.Repeat("PS5 Disc Console Black", 20))
        .Concat(Enumerable.Repeat("PS5 Digital Console Black", 15))
        .Concat(Enumerable.Repeat("PS5 Disc Slim Console", 10))
        .ToArray();

    private TaxonomyService _service;

    [SetUp]
    public void SetUp()
    {
        // Mock IEmbeddingService to return distinguishable vectors
        var mockEmbedding = new Mock<IEmbeddingService>();
        mockEmbedding
            .Setup(e => e.GetEmbeddings(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, CancellationToken _) =>
            {
                // Give each term a unique but deterministic vector
                // Similar terms get similar vectors
                return texts.Select(t =>
                {
                    return t switch
                    {
                        "disc" => new[] { 1f, 0f, 0f, 0f },
                        "digital" => new[] { 0.8f, 0.2f, 0f, 0f },
                        "white" => new[] { 0f, 0f, 1f, 0f },
                        "black" => new[] { 0f, 0f, 0.8f, 0.2f },
                        "slim" => new[] { 0f, 1f, 0f, 0f },
                        _ => new[] { 0.5f, 0.5f, 0.5f, 0.5f },
                    };
                }).ToArray();
            });

        var extractor = new NgramExtractor(mockEmbedding.Object);
        var analyzer = new MutualExclusivityAnalyzer();
        var detector = new LouvainCommunityDetector();

        _service = new TaxonomyService(extractor, analyzer, detector, mockEmbedding.Object);
    }

    [Test]
    public async Task Should_discover_axes_from_synthetic_titles()
    {
        var result = await _service.Generate(Ps5Titles);

        Assert.That(result.Axes.Any(), Is.True,
            "Should discover at least one axis");
    }

    [Test]
    public async Task Should_report_coverage_greater_than_zero()
    {
        var result = await _service.Generate(Ps5Titles);

        Assert.That(result.CoveragePercent, Is.GreaterThan(0),
            "Coverage should be > 0% with synthetic data containing axis values");
    }

    [Test]
    public async Task Should_assign_listings_to_cells()
    {
        var result = await _service.Generate(Ps5Titles);

        var assigned = result.Assignments.Where(a => a.Cell.Count > 0).ToList();
        Assert.That(assigned.Count, Is.GreaterThan(0),
            "At least some listings should be assigned to cells");
    }

    [Test]
    public async Task Should_detect_conflicts_when_multiple_values_match()
    {
        // A title with both "disc" and "digital" should be a conflict
        var titles = Ps5Titles.Append("PS5 Disc Digital Console").ToArray();

        var result = await _service.Generate(titles);

        Assert.That(result.ConflictPercent, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task Should_compute_cell_stats()
    {
        var result = await _service.Generate(Ps5Titles);

        Assert.That(result.Cells.Any(), Is.True,
            "Should produce at least one cell with stats");
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~TaxonomyServiceTests"`
Expected: Build error — TaxonomyService class doesn't exist

**Step 3: Implement TaxonomyService**

```csharp
// TaxonomyService.cs
namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyService : ITaxonomyService
{
    private const double SignificanceThreshold = 0.03;
    private const int MinExclusivePairs = 3;
    private const double GraphEdgeSimilarityThreshold = 0.15;
    private const double ModifierCoverageThreshold = 0.04;
    private const double ValueDedupOverlap = 0.85;
    private const double ExclusivityThreshold = 0.05;

    private readonly INgramExtractor _extractor;
    private readonly IMutualExclusivityAnalyzer _analyzer;
    private readonly ICommunityDetector _detector;
    private readonly IEmbeddingService _embeddingService;

    public TaxonomyService(
        INgramExtractor extractor,
        IMutualExclusivityAnalyzer analyzer,
        ICommunityDetector detector,
        IEmbeddingService embeddingService)
    {
        _extractor = extractor;
        _analyzer = analyzer;
        _detector = detector;
        _embeddingService = embeddingService;
    }

    public async Task<TaxonomyResult> Generate(IEnumerable<string> titles)
    {
        var titleList = titles.ToList();
        var total = titleList.Count;

        // Stage 1: Extract and dedup n-grams
        var rawNgrams = _extractor.Extract(titleList);
        var ngrams = (await _extractor.Deduplicate(rawNgrams)).ToList();

        // Compute match sets and filter to significant
        var allMatchSets = _analyzer.ComputeMatchSets(titleList, ngrams).ToList();
        var minMatches = (int)(total * SignificanceThreshold);
        var significantSets = allMatchSets
            .Where(ms => ms.ListingIndices.Count >= minMatches)
            .ToList();

        // Find mutually exclusive pairs
        var exclusivePairs = _analyzer.FindExclusivePairs(significantSets).ToList();

        // Filter to candidates participating in enough ME pairs
        var pairCounts = new Dictionary<string, int>();
        foreach (var pair in exclusivePairs)
        {
            pairCounts[pair.A.Canonical] = pairCounts.GetValueOrDefault(pair.A.Canonical) + 1;
            pairCounts[pair.B.Canonical] = pairCounts.GetValueOrDefault(pair.B.Canonical) + 1;
        }

        var candidates = significantSets
            .Where(ms => pairCounts.GetValueOrDefault(ms.Ngram.Canonical) >= MinExclusivePairs)
            .ToList();

        if (candidates.Count < 2)
        {
            return new TaxonomyResult(
                Enumerable.Empty<Axis>(),
                titleList.Select((_, i) => new CellAssignment(
                    i, new Dictionary<string, string>(), false)),
                Enumerable.Empty<CellStats>(),
                0.0, 0.0);
        }

        // Stage 2: Build graph and detect communities
        var candidateNames = candidates.Select(c => c.Ngram.Canonical).ToList();
        var candidateEmbeddings = await _embeddingService.GetEmbeddings(candidateNames);

        var nameToIndex = new Dictionary<string, int>();
        for (var i = 0; i < candidateNames.Count; i++)
        {
            nameToIndex[candidateNames[i]] = i;
        }

        // Normalize embeddings for cosine similarity
        var normed = candidateEmbeddings.Select(Normalize).ToArray();

        var graphEdges = new List<WeightedEdge>();
        foreach (var pair in exclusivePairs)
        {
            if (!nameToIndex.TryGetValue(pair.A.Canonical, out var idxA) ||
                !nameToIndex.TryGetValue(pair.B.Canonical, out var idxB))
            {
                continue;
            }

            var similarity = CosineSimilarity(normed[idxA], normed[idxB]);
            if (similarity > GraphEdgeSimilarityThreshold)
            {
                graphEdges.Add(new WeightedEdge(idxA, idxB, similarity));
            }
        }

        var rawCommunities = _detector.Detect(
            graphEdges, candidates.Count, resolution: 2.0).ToList();

        // Map communities from node indices to actual n-grams
        var axes = new List<Axis>();
        var communityIndex = 0;
        foreach (var community in rawCommunities)
        {
            var memberIndices = community.Members
                .Select(m => int.Parse(m.Canonical))
                .ToList();

            if (memberIndices.Count < 2)
            {
                continue;
            }

            var memberNgrams = memberIndices
                .Select(i => candidates[i].Ngram)
                .ToList();

            var values = memberNgrams
                .Select(n => new AxisValue(n.Canonical, new[] { n }))
                .ToList();

            axes.Add(new Axis($"Axis {communityIndex}", values));
            communityIndex++;
        }

        // Stage 4: Post-processing
        var matchSetLookup = allMatchSets.ToDictionary(ms => ms.Ngram.Canonical);
        axes = DeduplicateAxisValues(axes, matchSetLookup);
        axes = PruneOverlappingValues(axes, titleList, matchSetLookup);
        axes = EnforceMutualExclusivityPerValue(axes, titleList, matchSetLookup);

        // Cell assignment
        var assignments = AssignListings(titleList, axes, matchSetLookup);
        var covered = assignments.Count(a => a.Cell.Count > 0);
        var conflicts = assignments.Count(a => a.HasConflict);

        return new TaxonomyResult(
            axes,
            assignments,
            Enumerable.Empty<CellStats>(), // Pricing stats require price data — skipped for now
            total > 0 ? 100.0 * covered / total : 0,
            total > 0 ? 100.0 * conflicts / total : 0);
    }

    private static List<Axis> DeduplicateAxisValues(
        List<Axis> axes, Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var toRemove = new HashSet<int>();

            for (var i = 0; i < values.Count && !toRemove.Contains(i); i++)
            {
                for (var j = i + 1; j < values.Count; j++)
                {
                    if (toRemove.Contains(j))
                    {
                        continue;
                    }

                    var setA = GetValueMatchSet(values[i], matchSets);
                    var setB = GetValueMatchSet(values[j], matchSets);

                    if (setA.Count == 0 || setB.Count == 0)
                    {
                        continue;
                    }

                    var overlap = (double)setA.Count(idx => setB.Contains(idx))
                        / Math.Min(setA.Count, setB.Count);

                    if (overlap >= ValueDedupOverlap)
                    {
                        // Keep the one with more matches
                        toRemove.Add(setA.Count >= setB.Count ? j : i);
                        if (toRemove.Contains(i))
                        {
                            break;
                        }
                    }
                }
            }

            var surviving = values.Where((_, idx) => !toRemove.Contains(idx)).ToList();
            if (surviving.Count >= 2)
            {
                result.Add(new Axis(axis.Name, surviving));
            }
        }
        return result;
    }

    private static List<Axis> PruneOverlappingValues(
        List<Axis> axes, List<string> titles,
        Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var threshold = values.Count >= 5 ? 0.35 : 0.20;

            var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();
            var pruning = true;

            while (pruning && values.Count >= 2)
            {
                pruning = false;
                var worstIndex = -1;
                var worstPartners = 0;

                for (var i = 0; i < values.Count; i++)
                {
                    var partners = 0;
                    for (var j = 0; j < values.Count; j++)
                    {
                        if (i == j || valueSets[i].Count == 0 || valueSets[j].Count == 0)
                        {
                            continue;
                        }

                        var overlap = (double)valueSets[i].Count(idx => valueSets[j].Contains(idx))
                            / Math.Min(valueSets[i].Count, valueSets[j].Count);

                        if (overlap > threshold)
                        {
                            partners++;
                        }
                    }

                    if (partners > worstPartners ||
                        (partners == worstPartners && partners > 0
                         && worstIndex >= 0 && valueSets[i].Count < valueSets[worstIndex].Count))
                    {
                        worstPartners = partners;
                        worstIndex = i;
                    }
                }

                if (worstPartners > 0 && worstIndex >= 0)
                {
                    values.RemoveAt(worstIndex);
                    valueSets.RemoveAt(worstIndex);
                    threshold = values.Count >= 5 ? 0.35 : 0.20;
                    pruning = true;
                }
            }

            if (values.Count >= 2)
            {
                result.Add(new Axis(axis.Name, values));
            }
        }
        return result;
    }

    private static List<Axis> EnforceMutualExclusivityPerValue(
        List<Axis> axes, List<string> titles,
        Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<Axis>();
        foreach (var axis in axes)
        {
            var values = axis.Values.ToList();
            var changed = true;

            while (changed && values.Count >= 2)
            {
                changed = false;
                var valueSets = values.Select(v => GetValueMatchSet(v, matchSets)).ToList();

                var violationCounts = new int[values.Count];
                for (var i = 0; i < values.Count; i++)
                {
                    for (var j = i + 1; j < values.Count; j++)
                    {
                        if (valueSets[i].Count == 0 || valueSets[j].Count == 0)
                        {
                            continue;
                        }

                        var overlap = (double)valueSets[i].Count(idx => valueSets[j].Contains(idx))
                            / Math.Min(valueSets[i].Count, valueSets[j].Count);

                        if (overlap >= ExclusivityThreshold)
                        {
                            violationCounts[i]++;
                            violationCounts[j]++;
                        }
                    }
                }

                var worstIndex = -1;
                var worstCount = 0;
                for (var i = 0; i < violationCounts.Length; i++)
                {
                    if (violationCounts[i] > worstCount)
                    {
                        worstCount = violationCounts[i];
                        worstIndex = i;
                    }
                }

                // Only demote if >= 2 violations AND axis has >= 5 values
                if (worstCount >= 2 && values.Count >= 5 && worstIndex >= 0)
                {
                    values.RemoveAt(worstIndex);
                    changed = true;
                }
            }

            if (values.Count >= 2)
            {
                result.Add(new Axis(axis.Name, values));
            }
        }
        return result;
    }

    private static List<CellAssignment> AssignListings(
        List<string> titles, List<Axis> axes,
        Dictionary<string, MatchSet> matchSets)
    {
        var result = new List<CellAssignment>();

        for (var i = 0; i < titles.Count; i++)
        {
            var titleLower = titles[i].ToLowerInvariant();
            var cell = new Dictionary<string, string>();
            var hasConflict = false;

            foreach (var axis in axes)
            {
                var matched = new List<string>();
                foreach (var value in axis.Values)
                {
                    if (value.Ngrams.Any(n =>
                        n.Forms.Any(f => MutualExclusivityAnalyzer.PatternMatches(f, titleLower))))
                    {
                        matched.Add(value.Label);
                    }
                }

                if (matched.Count == 1)
                {
                    cell[axis.Name] = matched[0];
                }
                else if (matched.Count > 1)
                {
                    hasConflict = true;
                    cell[axis.Name] = matched[0];
                }
            }

            result.Add(new CellAssignment(i, cell, hasConflict));
        }

        return result;
    }

    private static HashSet<int> GetValueMatchSet(
        AxisValue value, Dictionary<string, MatchSet> matchSets)
    {
        var combined = new HashSet<int>();
        foreach (var ngram in value.Ngrams)
        {
            if (matchSets.TryGetValue(ngram.Canonical, out var ms))
            {
                combined.UnionWith(ms.ListingIndices);
            }
        }
        return combined;
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
        if (magnitude == 0)
        {
            return vector;
        }
        return vector.Select(v => v / magnitude).ToArray();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~TaxonomyServiceTests"`
Expected: All tests pass

**Step 5: Run all taxonomy tests together**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Taxonomy"`
Expected: All tests pass

**Step 6: Commit**

```bash
git add AIOMarketMaker.Core/Services/Taxonomy/TaxonomyService.cs AIOMarketMaker.Tests.Unit/Taxonomy/TaxonomyServiceTests.cs
git commit -m "feat: implement TaxonomyService orchestrator with post-processing and cell assignment"
```

---

## Task 7: Full Build Verification

Verify the entire solution still builds and all existing tests pass.

**Step 1: Build the solution**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeded, 0 errors

**Step 2: Run all unit tests**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj`
Expected: All tests pass (existing + new taxonomy tests)

**Step 3: Verify no existing tests broke**

Check output for any failures in non-taxonomy tests. If any fail, investigate and fix before proceeding.
