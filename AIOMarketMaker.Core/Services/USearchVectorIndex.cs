using System.Collections.Concurrent;
using System.Text.Json;
using Cloud.Unum.USearch;

namespace AIOMarketMaker.Core.Services;

public class USearchVectorIndex : IVectorIndex, IDisposable
{
    private readonly VectorIndexConfig _config;
    private readonly ReaderWriterLockSlim _rwLock = new();
    private bool _disposed;
    private USearchIndex _index;
    private ConcurrentDictionary<string, ulong> _idToKey = new();
    private ConcurrentDictionary<ulong, string> _keyToId = new();
    private ulong _nextKey;

    public int Count => (int)_idToKey.Count;

    public USearchVectorIndex(VectorIndexConfig config)
    {
        _config = config;
        _index = CreateNewIndex();
    }

    public void Upsert(string id, float[] vector)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_idToKey.TryGetValue(id, out var existingKey))
            {
                _index.Remove(existingKey);
                _index.Add(existingKey, vector);
            }
            else
            {
                var key = _nextKey++;
                _idToKey[id] = key;
                _keyToId[key] = id;
                _index.Add(key, vector);
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void UpsertBatch(IEnumerable<(string Id, float[] Vector)> items)
    {
        foreach (var (id, vector) in items)
        {
            Upsert(id, vector);
        }
    }

    public IEnumerable<VectorSearchHit> Search(float[] queryVector, int topK)
    {
        _rwLock.EnterReadLock();
        try
        {
            return SearchCore(queryVector, topK);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public IEnumerable<VectorSearchHit> SearchById(string id, int topK)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_idToKey.TryGetValue(id, out var key))
            {
                return Enumerable.Empty<VectorSearchHit>();
            }

            var found = _index.Get(key, out float[] vector);
            if (found == 0)
            {
                return Enumerable.Empty<VectorSearchHit>();
            }

            return SearchCore(vector, topK);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Remove(IEnumerable<string> ids)
    {
        _rwLock.EnterWriteLock();
        try
        {
            foreach (var id in ids)
            {
                if (_idToKey.TryRemove(id, out var key))
                {
                    _keyToId.TryRemove(key, out _);
                    _index.Remove(key);
                }
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public bool Contains(string id)
    {
        _rwLock.EnterReadLock();
        try
        {
            return _idToKey.ContainsKey(id);
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Save()
    {
        _rwLock.EnterWriteLock();
        try
        {
            var indexDir = Path.GetDirectoryName(_config.IndexPath);
            if (!string.IsNullOrEmpty(indexDir))
            {
                Directory.CreateDirectory(indexDir);
            }

            _index.Save(_config.IndexPath);

            var idMap = new Dictionary<string, ulong>(_idToKey);
            var json = JsonSerializer.Serialize(idMap);
            File.WriteAllText(_config.IdMapPath, json);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void Load()
    {
        if (!File.Exists(_config.IndexPath) || !File.Exists(_config.IdMapPath))
        {
            return;
        }

        _rwLock.EnterWriteLock();
        try
        {
            _index.Dispose();
            _index = new USearchIndex(_config.IndexPath);

            var json = File.ReadAllText(_config.IdMapPath);
            var idMap = JsonSerializer.Deserialize<Dictionary<string, ulong>>(json)
                        ?? new Dictionary<string, ulong>();

            _idToKey = new ConcurrentDictionary<string, ulong>(idMap);
            _keyToId = new ConcurrentDictionary<ulong, string>();

            ulong maxKey = 0;
            foreach (var (id, key) in _idToKey)
            {
                _keyToId[key] = id;
                if (key >= maxKey)
                {
                    maxKey = key + 1;
                }
            }

            _nextKey = maxKey;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _index.Dispose();
            _rwLock.Dispose();
            _disposed = true;
        }
    }

    private IEnumerable<VectorSearchHit> SearchCore(float[] queryVector, int topK)
    {
        if (_idToKey.IsEmpty)
        {
            return Enumerable.Empty<VectorSearchHit>();
        }

        var effectiveTopK = Math.Min(topK, (int)_idToKey.Count);

        int matchCount = _index.Search(queryVector, effectiveTopK, out var keys, out var distances);

        var results = new List<VectorSearchHit>(matchCount);
        for (var i = 0; i < matchCount; i++)
        {
            if (_keyToId.TryGetValue(keys[i], out var id))
            {
                var similarity = 1f - distances[i];
                results.Add(new VectorSearchHit(id, similarity));
            }
        }

        return results;
    }

    private USearchIndex CreateNewIndex()
    {
        return new USearchIndex(
            metricKind: MetricKind.Cos,
            quantization: ScalarKind.Float32,
            dimensions: (ulong)_config.Dimensions,
            connectivity: (ulong)_config.Connectivity,
            expansionAdd: (ulong)_config.ExpansionAdd,
            expansionSearch: (ulong)_config.ExpansionSearch
        );
    }
}
