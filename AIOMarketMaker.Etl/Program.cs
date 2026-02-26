using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl;
using AIOMarketMaker.Etl.Commands;
using Serilog;

Configure.ConfigureSerilog();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(Configure.ConfigureAppConfiguration)
    .ConfigureServices(Configure.ConfigureServices)
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .UseSerilog()
    .Build();

if (args.Contains("--reindex-missing"))
{
    await ReindexMissingCommand.Run(host);
    return;
}

if (args.Contains("--batch-label"))
{
    await BatchLabelCommand.Run(host, args);
    return;
}

if (args.Contains("--comparables"))
{
    await ComparablesCommand.Run(host, args);
    return;
}

if (args.Contains("--k-analysis"))
{
    await KAnalysisCommand.Run(host, args);
    return;
}

if (args.Contains("--validate"))
{
    await ValidationCommand.Run(host, args);
    return;
}

if (args.Contains("--backfill-confidence"))
{
    await BackfillConfidenceCommand.Run(host, args);
    return;
}
