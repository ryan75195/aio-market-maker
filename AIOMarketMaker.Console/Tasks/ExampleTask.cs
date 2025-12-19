namespace AIOMarketMaker.Console.Tasks;

public class ExampleTask : ITask
{
    public string Name => "example";
    public string Description => "Example stub task";

    public Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        System.Console.WriteLine("Example task executed with args: " + string.Join(", ", args));
        return Task.FromResult(0);
    }
}
