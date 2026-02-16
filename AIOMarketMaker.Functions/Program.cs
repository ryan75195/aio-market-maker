using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Serilog;
using Serilog.Formatting.Compact;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

// Configure Serilog with optional file logging based on LOG_SESSION_PATH
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Functions")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    Directory.CreateDirectory(logSessionPath);
    var logFile = Path.Combine(logSessionPath, "functions.json");
    loggerConfig.WriteTo.File(
        new CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
}

Log.Logger = loggerConfig.CreateLogger();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // SQL Server database - required for the HTTP API
        var sqlConnectionString = configuration.GetValue<string>("SqlConnectionString");
        if (!string.IsNullOrEmpty(sqlConnectionString))
        {
            services.AddDbContext<EtlDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));
        }

        // Blob storage - for clearing HTML files
        var blobConnectionString = configuration.GetValue<string>("blobStorageConnectionString")
            ?? configuration.GetValue<string>("AzureWebJobsStorage");
        if (!string.IsNullOrEmpty(blobConnectionString))
        {
            services.AddSingleton(new BlobServiceClient(blobConnectionString));
        }

        // Vector index - for clearing on data reset (optional)
        var vectorIndexPath = configuration.GetValue<string>("VectorIndex:IndexPath");
        if (!string.IsNullOrEmpty(vectorIndexPath))
        {
            var vectorIndexConfig = new VectorIndexConfig(
                IndexPath: vectorIndexPath,
                IdMapPath: configuration.GetValue<string>("VectorIndex:IdMapPath")
                    ?? Path.ChangeExtension(vectorIndexPath, ".idmap.json"));
            services.AddSingleton(vectorIndexConfig);
            services.AddSingleton<IVectorIndex>(sp =>
            {
                var config = sp.GetRequiredService<VectorIndexConfig>();
                var index = new USearchVectorIndex(config);
                if (File.Exists(config.IndexPath) && File.Exists(config.IdMapPath))
                {
                    index.Load();
                }
                return index;
            });
        }
    })
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddSerilog(Log.Logger);
    })
    .Build();

host.Run();
