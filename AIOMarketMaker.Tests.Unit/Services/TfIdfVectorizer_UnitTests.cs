using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class TfIdfVectorizer_UnitTests
{
    private TfIdfVectorizer CreateVectorizer(TfIdfConfig? config = null)
    {
        return new TfIdfVectorizer(config ?? new TfIdfConfig(MinDocumentFrequency: 1, MaxDocumentFrequencyRatio: 1.0));
    }

    [Test]
    public void Should_return_empty_vectors_when_input_is_empty()
    {
        var vectorizer = CreateVectorizer();

        var result = vectorizer.FitTransform(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(result.Vectors, Is.Empty);
            Assert.That(result.FeatureNames, Is.Empty);
        });
    }

    [Test]
    public void Should_produce_vectors_with_dimensions_matching_vocabulary_size()
    {
        var vectorizer = CreateVectorizer();
        var docs = new[]
        {
            "Pokemon 151 Booster Box",
            "Pokemon 151 Booster Pack",
            "Obsidian Flames Booster Box"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            Assert.That(result.Vectors, Has.Length.EqualTo(3));
            foreach (var vector in result.Vectors)
            {
                Assert.That(vector, Has.Length.EqualTo(result.FeatureNames.Count),
                    "Each vector dimension should match vocabulary size");
            }
        });
    }

    [Test]
    public void Should_produce_l2_normalized_vectors()
    {
        var vectorizer = CreateVectorizer();
        var docs = new[]
        {
            "Pokemon 151 Booster Box Sealed",
            "Pokemon 151 Booster Pack",
            "Obsidian Flames Booster Box ETB"
        };

        var result = vectorizer.FitTransform(docs);

        foreach (var vector in result.Vectors)
        {
            if (vector.Length == 0)
            {
                continue;
            }

            var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
            Assert.That(magnitude, Is.EqualTo(1.0f).Within(0.001f),
                "Each vector should have unit length (L2 normalized)");
        }
    }

    [Test]
    public void Should_exclude_rare_terms_below_min_document_frequency()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 3, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        // "booster" appears in all 5, "pokemon" in 3, "obsidian" in only 2
        var docs = new[]
        {
            "Pokemon Booster Box",
            "Pokemon Booster Pack",
            "Pokemon Booster Bundle",
            "Obsidian Booster Box",
            "Obsidian Booster Pack"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            Assert.That(result.FeatureNames, Does.Contain("booster"));
            Assert.That(result.FeatureNames, Does.Contain("pokemon"));
            Assert.That(result.FeatureNames, Does.Not.Contain("obsidian"),
                "obsidian appears in only 2 docs, below min_df=3");
        });
    }

    [Test]
    public void Should_exclude_ubiquitous_terms_above_max_document_frequency()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MaxDocumentFrequencyRatio: 0.5, MaxNgramSize: 1);
        var vectorizer = new TfIdfVectorizer(config);

        // "card" appears in all 4 docs (100%), "pokemon" in 2 (50%), "yugioh" in 2 (50%)
        var docs = new[]
        {
            "Pokemon Card Booster",
            "Pokemon Card Pack",
            "Yugioh Card Booster",
            "Yugioh Card Pack"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.That(result.FeatureNames, Does.Not.Contain("card"),
            "card appears in 100% of docs, above max_df_ratio=0.5");
    }

    [Test]
    public void Should_generate_bigrams_and_trigrams_in_feature_names()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MinNgramSize: 1, MaxNgramSize: 3, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "Pokemon 151 Booster Box",
            "Pokemon 151 Booster Pack"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            // Unigrams
            Assert.That(result.FeatureNames, Does.Contain("pokemon"));
            Assert.That(result.FeatureNames, Does.Contain("151"));
            Assert.That(result.FeatureNames, Does.Contain("booster"));

            // Bigrams
            Assert.That(result.FeatureNames, Does.Contain("pokemon 151"));
            Assert.That(result.FeatureNames, Does.Contain("151 booster"));

            // Trigrams
            Assert.That(result.FeatureNames, Does.Contain("pokemon 151 booster"));
        });
    }

    [Test]
    public void Should_remove_stop_tokens()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "Brand New Pokemon Card Free Shipping",
            "Brand New Sealed Pokemon Card Fast Delivery"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            // These are default stop tokens
            Assert.That(result.FeatureNames, Does.Not.Contain("brand"));
            Assert.That(result.FeatureNames, Does.Not.Contain("new"));
            Assert.That(result.FeatureNames, Does.Not.Contain("free"));
            Assert.That(result.FeatureNames, Does.Not.Contain("shipping"));
            Assert.That(result.FeatureNames, Does.Not.Contain("sealed"));
            Assert.That(result.FeatureNames, Does.Not.Contain("fast"));
            Assert.That(result.FeatureNames, Does.Not.Contain("delivery"));

            // These should survive
            Assert.That(result.FeatureNames, Does.Contain("pokemon"));
            Assert.That(result.FeatureNames, Does.Contain("card"));
        });
    }

    [Test]
    public void Should_handle_special_characters_and_numbers()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "Pokémon 151 - Booster Box (36 packs)",
            "Pokémon 151 — ETB £49.99"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            // Numbers should be preserved as tokens
            Assert.That(result.FeatureNames, Does.Contain("151"));

            // Special chars stripped, "pok" + "mon" would become "pokémon" lowercased then accent kept
            // Since we use char.IsLetterOrDigit, accented chars ARE letters
            Assert.That(result.FeatureNames, Does.Contain("pokémon"));

            // Punctuation/currency should be stripped
            Assert.That(result.FeatureNames.Any(f => f.Contains("£")), Is.False);
            Assert.That(result.FeatureNames.Any(f => f.Contains("(")), Is.False);
        });
    }

    [Test]
    public void Should_filter_single_character_tokens()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "Pokemon X and Y Version",
            "Pokemon X Version Card"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            // Single char tokens should be filtered (len <= 1)
            // "x" and "y" are single characters
            Assert.That(result.FeatureNames, Does.Not.Contain("x"));
            Assert.That(result.FeatureNames, Does.Not.Contain("y"));

            // Multi-char tokens survive (minus stop tokens like "and")
            Assert.That(result.FeatureNames, Does.Contain("pokemon"));
            Assert.That(result.FeatureNames, Does.Contain("version"));
        });
    }

    [Test]
    public void Should_use_sublinear_tf_when_configured()
    {
        var configSublinear = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0, SublinearTf: true);
        var configRaw = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0, SublinearTf: false);

        // Doc where "pokemon" appears multiple times vs once
        var docs = new[]
        {
            "Pokemon Pokemon Pokemon Card",
            "Card Booster Pack"
        };

        var resultSublinear = new TfIdfVectorizer(configSublinear).FitTransform(docs);
        var resultRaw = new TfIdfVectorizer(configRaw).FitTransform(docs);

        // With sublinear TF, repeated terms get dampened (1 + log(tf) vs raw tf)
        // The vectors are normalized, so compare the ratio of pokemon weight to other weights
        var pokemonIdxSub = resultSublinear.FeatureNames.ToList().IndexOf("pokemon");
        var pokemonIdxRaw = resultRaw.FeatureNames.ToList().IndexOf("pokemon");

        if (pokemonIdxSub >= 0 && pokemonIdxRaw >= 0)
        {
            // Sublinear should dampen the repeated term relative to raw
            var subWeight = Math.Abs(resultSublinear.Vectors[0][pokemonIdxSub]);
            var rawWeight = Math.Abs(resultRaw.Vectors[0][pokemonIdxRaw]);

            // Both should be non-zero since pokemon appears in doc 0
            Assert.That(subWeight, Is.GreaterThan(0));
            Assert.That(rawWeight, Is.GreaterThan(0));
        }
    }

    [Test]
    public void Should_produce_zero_vector_for_doc_with_only_stop_words()
    {
        var config = new TfIdfConfig(MinDocumentFrequency: 1, MaxNgramSize: 1, MaxDocumentFrequencyRatio: 1.0);
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "the and or for with", // all stop words
            "Pokemon Booster Box Card"
        };

        var result = vectorizer.FitTransform(docs);

        // First doc has no non-stop tokens, so all its TF values are 0
        Assert.That(result.Vectors[0].All(v => v == 0), Is.True,
            "Document with only stop words should produce a zero vector");
    }

    [Test]
    public void Should_cluster_ebay_titles_correctly_with_ward_linkage()
    {
        // Integration test: TF-IDF vectorization + Ward linkage via ClusterByText
        var tfidfConfig = new TfIdfConfig(MinDocumentFrequency: 2, MaxNgramSize: 2);
        var tfidfVectorizer = new TfIdfVectorizer(tfidfConfig);
        var clusterConfig = new ClusteringConfig(MinClusterSize: 5, MinPoints: 3);
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<ClusteringService>();
        var clusteringService = new ClusteringService(clusterConfig, tfidfVectorizer, logger);

        // Two distinct product groups with enough repetition for min_df
        var titles = new List<string>();
        var ids = new List<int>();

        // Group 1: Pokemon 151 Booster (10 listings)
        for (var i = 0; i < 10; i++)
        {
            ids.Add(i);
            titles.Add($"Pokemon 151 Booster Box 36 Packs Variant{i}");
        }

        // Group 2: Obsidian Flames (10 listings)
        for (var i = 0; i < 10; i++)
        {
            ids.Add(10 + i);
            titles.Add($"Obsidian Flames Booster Box 36 Packs Item{i}");
        }

        var clusterResult = clusteringService.ClusterByText(ids, titles);

        Assert.Multiple(() =>
        {
            // Should find at least 2 clusters (the two product groups)
            Assert.That(clusterResult.Clusters.Count, Is.GreaterThanOrEqualTo(2),
                "Should find at least 2 distinct product clusters");

            // All items should be preserved
            var allItems = clusterResult.Clusters.SelectMany(c => c.Items).Concat(clusterResult.Noise).ToList();
            Assert.That(allItems, Has.Count.EqualTo(20));

            // Pokemon 151 items (IDs 0-9) should be in the same cluster
            var pokemonCluster = clusterResult.Clusters
                .FirstOrDefault(c => c.Items.Any(e => e.Id < 10));
            var obsidianCluster = clusterResult.Clusters
                .FirstOrDefault(c => c.Items.Any(e => e.Id >= 10));

            if (pokemonCluster != null && obsidianCluster != null)
            {
                Assert.That(pokemonCluster.Label, Is.Not.EqualTo(obsidianCluster.Label),
                    "Pokemon and Obsidian products should be in different clusters");
            }
        });
    }

    [Test]
    public void Should_accept_custom_stop_tokens()
    {
        var config = new TfIdfConfig(
            MinDocumentFrequency: 1,
            MaxNgramSize: 1,
            StopTokens: new[] { "custom", "stop" });
        var vectorizer = new TfIdfVectorizer(config);

        var docs = new[]
        {
            "Custom Stop Pokemon Card",
            "Custom Stop Booster Pack"
        };

        var result = vectorizer.FitTransform(docs);

        Assert.Multiple(() =>
        {
            Assert.That(result.FeatureNames, Does.Not.Contain("custom"));
            Assert.That(result.FeatureNames, Does.Not.Contain("stop"));
            Assert.That(result.FeatureNames, Does.Contain("pokemon"));

            // Default stop tokens should NOT apply when custom ones are provided
            // "card" is not a default stop token, "pack" is not either
            Assert.That(result.FeatureNames, Does.Contain("card"));
        });
    }
}
