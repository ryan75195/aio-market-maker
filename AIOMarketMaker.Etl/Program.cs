using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl;
using AIOMarketMaker.Etl.Commands;
using Serilog;

Setup.ConfigureSerilog();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(Setup.ConfigureAppConfiguration)
    .ConfigureServices(Setup.ConfigureServices)
    .ConfigureLogging(logging =>
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
    })
    .UseSerilog()
    .Build();

if (args.Contains("--export-vectors"))
{
    await ExportVectorsCommand.Run(host, args);
    return;
}

if (args.Contains("--benchmark"))
{
    await BenchmarkCommand.Run(host);
    return;
}

if (args.Contains("--reindex-missing"))
{
    await ReindexMissingCommand.Run(host);
    return;
}

if (args.Contains("--clean-descriptions"))
{
    var limit = CommandHelpers.GetIntArg(args, "--limit");
    await CleanDescriptionsCommand.Run(host, limit);
    return;
}

if (args.Contains("--backfill-descriptions"))
{
    var limit = CommandHelpers.GetIntArg(args, "--limit");
    await BackfillDescriptionsCommand.Run(host, limit);
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
