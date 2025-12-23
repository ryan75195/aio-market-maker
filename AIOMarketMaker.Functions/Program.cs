using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

Console.WriteLine("=== Azure Functions Host Starting ===");

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        Console.WriteLine("=== ConfigureServices Starting ===");

        var configuration = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // SQL Server database (required for the Jobs API)
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
        Console.WriteLine($"SqlConnectionString configured: {!string.IsNullOrEmpty(sqlConnectionString)}");

        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
        }

        Console.WriteLine("=== ConfigureServices Complete ===");
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    })
    .Build();

Console.WriteLine("=== Host Built, Starting Run ===");

host.Run();
