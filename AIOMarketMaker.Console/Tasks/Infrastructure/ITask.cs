namespace AIOMarketMaker.Console.Tasks;

public interface ITask
{
    string Name { get; }
    string Description { get; }
    Task<int> ExecuteAsync(string[] args, CancellationToken ct = default);
}
