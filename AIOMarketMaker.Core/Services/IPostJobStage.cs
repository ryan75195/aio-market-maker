namespace AIOMarketMaker.Core.Services;

public record PostJobContext(int RunId, int JobId, string SearchTerm);

public interface IPostJobStage
{
    string Name { get; }
    Task Execute(PostJobContext context, CancellationToken ct = default);
}
