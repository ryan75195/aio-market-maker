namespace AIOMarketMaker.Core.Services.Pipeline;

public record BatchContext(Guid BatchId, IEnumerable<ScrapeJobConfig> Jobs, IEnumerable<int> RunIds);

public interface IBatchStage
{
    string Name { get; }
    Task Execute(BatchContext context, CancellationToken ct = default);
}
