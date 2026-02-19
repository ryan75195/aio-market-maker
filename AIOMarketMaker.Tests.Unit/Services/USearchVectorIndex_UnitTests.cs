using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class USearchVectorIndex_UnitTests
{
    private string _tempDir = null!;
    private VectorIndexConfig _config = null!;
    private USearchVectorIndex _index = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "usearch_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new VectorIndexConfig(
            IndexPath: Path.Combine(_tempDir, "test.usearch"),
            IdMapPath: Path.Combine(_tempDir, "test_idmap.json"),
            Dimensions: 4,
            Connectivity: 16,
            ExpansionAdd: 128,
            ExpansionSearch: 64
        );

        _index = new USearchVectorIndex(_config);
    }

    [TearDown]
    public void TearDown()
    {
        _index.Dispose();

        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void Should_start_empty()
    {
        Assert.That(_index.Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_upsert_and_contain()
    {
        _index.Upsert("item-1", new float[] { 1f, 0f, 0f, 0f });

        Assert.Multiple(() =>
        {
            Assert.That(_index.Contains("item-1"), Is.True);
            Assert.That(_index.Contains("item-2"), Is.False);
            Assert.That(_index.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_upsert_batch()
    {
        var items = new[]
        {
            ("a", new float[] { 1f, 0f, 0f, 0f }),
            ("b", new float[] { 0f, 1f, 0f, 0f }),
            ("c", new float[] { 0f, 0f, 1f, 0f })
        };

        _index.UpsertBatch(items);

        Assert.Multiple(() =>
        {
            Assert.That(_index.Count, Is.EqualTo(3));
            Assert.That(_index.Contains("a"), Is.True);
            Assert.That(_index.Contains("b"), Is.True);
            Assert.That(_index.Contains("c"), Is.True);
        });
    }

    [Test]
    public void Should_search_by_vector_and_return_most_similar()
    {
        _index.Upsert("north", new float[] { 1f, 0f, 0f, 0f });
        _index.Upsert("northeast", new float[] { 0.9f, 0.1f, 0f, 0f });
        _index.Upsert("south", new float[] { 0f, 0f, 0f, 1f });

        var results = _index.Search(new float[] { 1f, 0f, 0f, 0f }, topK: 3).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].Id, Is.EqualTo("north"));
            Assert.That(results[1].Id, Is.EqualTo("northeast"));
            Assert.That(results[2].Id, Is.EqualTo("south"));
        });
    }

    [Test]
    public void Should_search_by_id()
    {
        _index.Upsert("north", new float[] { 1f, 0f, 0f, 0f });
        _index.Upsert("northeast", new float[] { 0.9f, 0.1f, 0f, 0f });
        _index.Upsert("south", new float[] { 0f, 0f, 0f, 1f });

        var results = _index.SearchById("north", topK: 3).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[0].Id, Is.EqualTo("north"));
            Assert.That(results[1].Id, Is.EqualTo("northeast"));
        });
    }

    [Test]
    public void Should_remove_vectors()
    {
        _index.Upsert("keep", new float[] { 1f, 0f, 0f, 0f });
        _index.Upsert("remove", new float[] { 0f, 1f, 0f, 0f });

        _index.Remove(new[] { "remove" });

        Assert.Multiple(() =>
        {
            Assert.That(_index.Contains("keep"), Is.True);
            Assert.That(_index.Contains("remove"), Is.False);
            Assert.That(_index.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_handle_remove_nonexistent_id()
    {
        _index.Upsert("exists", new float[] { 1f, 0f, 0f, 0f });

        Assert.DoesNotThrow(() => _index.Remove(new[] { "nonexistent" }));
        Assert.That(_index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Should_overwrite_on_duplicate_upsert()
    {
        _index.Upsert("item", new float[] { 1f, 0f, 0f, 0f });
        _index.Upsert("item", new float[] { 0f, 0f, 0f, 1f });

        Assert.That(_index.Count, Is.EqualTo(1));

        // Search with the NEW vector direction - item should be most similar
        var results = _index.Search(new float[] { 0f, 0f, 0f, 1f }, topK: 1).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Id, Is.EqualTo("item"));
            Assert.That(results[0].Score, Is.GreaterThan(0.9f));
        });
    }

    [Test]
    public void Should_save_and_load()
    {
        _index.Upsert("persisted-1", new float[] { 1f, 0f, 0f, 0f });
        _index.Upsert("persisted-2", new float[] { 0f, 1f, 0f, 0f });
        _index.Save();

        using var loadedIndex = new USearchVectorIndex(_config);
        loadedIndex.Load();

        Assert.Multiple(() =>
        {
            Assert.That(loadedIndex.Count, Is.EqualTo(2));
            Assert.That(loadedIndex.Contains("persisted-1"), Is.True);
            Assert.That(loadedIndex.Contains("persisted-2"), Is.True);
        });

        // Verify search still works on loaded index
        var results = loadedIndex.Search(new float[] { 1f, 0f, 0f, 0f }, topK: 2).ToList();
        Assert.That(results[0].Id, Is.EqualTo("persisted-1"));
    }

    [Test]
    public void Should_return_cosine_similarity_scores()
    {
        _index.Upsert("identical", new float[] { 1f, 0f, 0f, 0f });

        var results = _index.Search(new float[] { 1f, 0f, 0f, 0f }, topK: 1).ToList();

        Assert.That(results[0].Score, Is.EqualTo(1.0f).Within(0.01f));
    }

    [Test]
    public void Should_return_empty_when_searching_empty_index()
    {
        var results = _index.Search(new float[] { 1f, 0f, 0f, 0f }, topK: 10).ToList();

        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Should_clamp_topk_to_index_count()
    {
        _index.Upsert("only-one", new float[] { 1f, 0f, 0f, 0f });

        var results = _index.Search(new float[] { 1f, 0f, 0f, 0f }, topK: 100).ToList();

        Assert.That(results, Has.Count.EqualTo(1));
    }
}
