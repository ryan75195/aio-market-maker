namespace AIOMarketMaker.Core.Services;

public record VectorSearchHit(string Id, float Score);

public interface IVectorIndex
{
    void Upsert(string id, float[] vector);
    void UpsertBatch(IEnumerable<(string Id, float[] Vector)> items);
    IEnumerable<VectorSearchHit> Search(float[] queryVector, int topK);
    IEnumerable<VectorSearchHit> SearchById(string id, int topK);
    void Remove(IEnumerable<string> ids);
    void Clear();
    bool Contains(string id);
    int Count { get; }
    void Save();
    void Load();
}
