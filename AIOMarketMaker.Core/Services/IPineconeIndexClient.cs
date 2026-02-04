using Pinecone;

namespace AIOMarketMaker.Core.Services;

public interface IPineconeIndexClient
{
    Task Upsert(UpsertRequest request, CancellationToken ct = default);
    Task<QueryResponse> Query(QueryRequest request, CancellationToken ct = default);
    Task Delete(DeleteRequest request, CancellationToken ct = default);
    Task<FetchResponse> Fetch(FetchRequest request, CancellationToken ct = default);
    Task Update(UpdateRequest request, CancellationToken ct = default);
}

public class PineconeIndexClientWrapper : IPineconeIndexClient
{
    private readonly IndexClient _index;

    public PineconeIndexClientWrapper(string apiKey, string indexName)
    {
        var client = new PineconeClient(apiKey);
        _index = client.Index(indexName);
    }

    public Task Upsert(UpsertRequest request, CancellationToken ct = default)
        => _index.UpsertAsync(request);

    public Task<QueryResponse> Query(QueryRequest request, CancellationToken ct = default)
        => _index.QueryAsync(request);

    public Task Delete(DeleteRequest request, CancellationToken ct = default)
        => _index.DeleteAsync(request);

    public Task<FetchResponse> Fetch(FetchRequest request, CancellationToken ct = default)
        => _index.FetchAsync(request);

    public Task Update(UpdateRequest request, CancellationToken ct = default)
        => _index.UpdateAsync(request);
}
