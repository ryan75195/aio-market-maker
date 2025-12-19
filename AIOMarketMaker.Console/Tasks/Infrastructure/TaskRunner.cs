namespace AIOMarketMaker.Console.Tasks;

public class TaskRunner
{
    private readonly IEnumerable<ITask> _tasks;

    public TaskRunner(IEnumerable<ITask> tasks)
    {
        _tasks = tasks;
    }

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var taskName = args.FirstOrDefault()?.ToLowerInvariant();

        if (string.IsNullOrEmpty(taskName) || taskName == "help")
        {
            PrintHelp();
            return 0;
        }

        var task = _tasks.FirstOrDefault(t => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase));
        if (task == null)
        {
            System.Console.WriteLine($"Unknown task: {taskName}\n");
            PrintHelp();
            return 1;
        }

        var taskArgs = args.Skip(1).ToArray();
        return await task.ExecuteAsync(taskArgs, ct);
    }

    private void PrintHelp()
    {
        System.Console.WriteLine("Usage: dotnet run -- <task> [args...]\n");
        System.Console.WriteLine("Available tasks:");
        foreach (var task in _tasks.OrderBy(t => t.Name))
        {
            System.Console.WriteLine($"  {task.Name,-20} {task.Description}");
        }
    }
}
