using AIOMarketMaker.Console;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.DependencyInjection;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = HostHelper.CreateHost(args);
        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var clusteringService = scope.ServiceProvider.GetRequiredService<IClusteringService>();
    }
}
