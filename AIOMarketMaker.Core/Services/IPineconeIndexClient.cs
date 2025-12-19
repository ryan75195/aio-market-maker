using Pinecone;

namespace AIOMarketMaker.Core.Services;

public interface IPineconeIndexClient
{
    Task UpsertAsync(UpsertRequest request, CancellationToken ct = default);
    Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default);
    Task DeleteAsync(DeleteRequest request, CancellationToken ct = default);
    Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken ct = default);
}

public class PineconeIndexClientWrapper : IPineconeIndexClient
{
    private readonly IndexClient _index;

    public PineconeIndexClientWrapper(string apiKey, string indexName)
    {
        var client = new PineconeClient(apiKey);
        _index = client.Index(indexName);
    }

    public Task UpsertAsync(UpsertRequest request, CancellationToken ct = default)
        => _index.UpsertAsync(request);

    public Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken ct = default)
        => _index.QueryAsync(request);

    public Task DeleteAsync(DeleteRequest request, CancellationToken ct = default)
        => _index.DeleteAsync(request);

    public Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken ct = default)
        => _index.FetchAsync(request);
}
