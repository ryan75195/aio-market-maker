using AIOMarketMaker.Console;
using AIOMarketMaker.Console.Tasks;
using Microsoft.Extensions.DependencyInjection;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var host = HostHelper.CreateHost(args);
        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<TaskRunner>();
        return await runner.RunAsync(args, CancellationToken.None);
    }
}
