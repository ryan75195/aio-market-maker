using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Pipeline;
using AIOMarketMaker.Core.Services.Taxonomy;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.Core.Parsers;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using AIOMarketMaker.Api.Services;
using Microsoft.Extensions.AI;
using Serilog;
using Serilog.Formatting.Compact;

// Configure Serilog with optional file sink
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");
if (string.IsNullOrEmpty(logSessionPath))
{
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var logsDir = Path.Combine(repoRoot, "logs");
    if (Directory.Exists(logsDir))
    {
        var latest = Directory.GetDirectories(logsDir, "session-*")
            .OrderDescending()
            .FirstOrDefault();
        logSessionPath = latest
            ?? Path.Combine(logsDir, $"session-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}");
    }
}

var loggerConfig = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "AIOMarketMaker.Api")
    .WriteTo.Console();

if (!string.IsNullOrEmpty(logSessionPath))
{
    Directory.CreateDirectory(logSessionPath);
    var logFile = Path.Combine(logSessionPath, "api.json");
    loggerConfig.WriteTo.File(
        new CompactJsonFormatter(),
        logFile,
        rollingInterval: RollingInterval.Hour,
        retainedFileCountLimit: null);
    Console.WriteLine($"Logging to: {logSessionPath}");
}

Log.Logger = loggerConfig.CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var configuration = builder.Configuration;

// Azure Storage clients
var storageConnectionString = configuration.GetValue<string>("AzureStorage:ConnectionString")
    ?? "UseDevelopmentStorage=true";
builder.Services.AddSingleton(_ => new BlobServiceClient(storageConnectionString));
builder.Services.AddSingleton(_ => new TableServiceClient(storageConnectionString));

// Job repository
builder.Services.AddSingleton<IJobRepository>(sp =>
    new AzureJobRepository(
        sp.GetRequiredService<TableServiceClient>(),
        sp.GetRequiredService<BlobServiceClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureJobRepository>>()));

// Database (SQL Server)
var sqlConnectionString = configuration.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("ConnectionStrings:SqlServer is required.");
var migrationRunner = new MigrationRunner(sqlConnectionString, null, useSqlServer: true);
migrationRunner.ApplyMigrations();
builder.Services.AddDbContextFactory<EtlDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

// Core parsing services
builder.Services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
builder.Services.AddSingleton<ISearchParser, EbaySearchParser>();
builder.Services.AddSingleton<IListingParser, EbayListingParser>();

// Web scraper client
var scraperBaseUrl = configuration.GetValue<string>("Scraper:BaseUrl") ?? "http://localhost:7126";
var scraperApiKey = configuration.GetValue<string>("Scraper:ApiKey") ?? "";
builder.Services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
builder.Services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
{
    client.BaseAddress = new Uri(scraperBaseUrl);
    client.Timeout = TimeSpan.FromMinutes(5);
});

// Embedding service
var openAiKey = configuration.GetValue<string>("OpenAi:ApiKey")
    ?? throw new InvalidOperationException("OpenAi:ApiKey is required.");
var embeddingModel = configuration.GetValue<string>("Embedding:Model") ?? "text-embedding-3-large";
var embeddingDimensions = configuration.GetValue<int>("Embedding:Dimensions", 3072);
builder.Services.AddSingleton(new EmbeddingConfig(openAiKey, embeddingModel, embeddingDimensions));
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();

// TF-IDF vectorizer (used by clustering service for text-based clustering)
builder.Services.AddSingleton(new TfIdfConfig());
builder.Services.AddSingleton<ITfIdfVectorizer, TfIdfVectorizer>();

// Clustering service
var clusteringConfig = new ClusteringConfig(
    MinClusterSize: configuration.GetValue<int>("Clustering:MinClusterSize", 8),
    MinPoints: configuration.GetValue<int>("Clustering:MinPoints", 4),
    Threshold: configuration.GetValue<double>("Clustering:Threshold", 1.5));
builder.Services.AddSingleton(clusteringConfig);
builder.Services.AddSingleton<IClusteringService, ClusteringService>();

// Pricing options
builder.Services.Configure<PricingOptions>(configuration.GetSection("Pricing"));

// Market listings query service
builder.Services.AddScoped<IMarketListingsQueryService, MarketListingsQueryService>();

// Chat agent
builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config["OpenAi:ApiKey"]
        ?? throw new InvalidOperationException("OpenAi:ApiKey is not configured");
    var model = config["OpenAi:ChatModel"] ?? "gpt-5-mini";
    return new OpenAI.Chat.ChatClient(model, apiKey).AsIChatClient();
});
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config["OpenAi:ApiKey"]
        ?? throw new InvalidOperationException("OpenAi:ApiKey is not configured");
    var model = config["OpenAi:AnnotationModel"] ?? "gpt-5-mini";
    return new OpenAI.Chat.ChatClient(model, apiKey);
});
builder.Services.AddScoped<IMarketsChatService, MarketsChatService>();

// Taxonomy LLM refiner
builder.Services.AddSingleton<ITaxonomyRefiner>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var apiKey = config["OpenAi:ApiKey"]
        ?? throw new InvalidOperationException("OpenAi:ApiKey is not configured");
    var model = config["OpenAi:TaxonomyModel"] ?? "gpt-4o-mini";
    var client = new OpenAI.Chat.ChatClient(model, apiKey);
    var logger = sp.GetRequiredService<ILogger<LlmTaxonomyRefiner>>();
    return new LlmTaxonomyRefiner(client, logger);
});

// Listing prediction service (reads existing predictions from DB)
builder.Services.AddScoped<IListingPredictionService, ListingPredictionService>();

// Taxonomy pipeline
builder.Services.AddSingleton<INgramExtractor, NgramExtractor>();
builder.Services.AddSingleton<IMutualExclusivityAnalyzer, MutualExclusivityAnalyzer>();
builder.Services.AddSingleton<ICommunityDetector, LouvainCommunityDetector>();
builder.Services.AddSingleton<ITaxonomyService, TaxonomyService>();
builder.Services.AddScoped<ITaxonomyPersistenceService, TaxonomyPersistenceService>();
builder.Services.AddSingleton<IPostJobStage, TaxonomyPostJobStage>();
builder.Services.AddSingleton<ICellPricingService, CellPricingService>();
builder.Services.AddScoped<ITaxonomyQueryService, TaxonomyQueryService>();
builder.Services.AddScoped<ITaxonomyOpportunityService, TaxonomyOpportunityService>();
builder.Services.AddSingleton<IPostJobStage, OpportunityPostJobStage>();

// Scraping concurrency config
var scrapingConfig = new ScrapingConfig(
    MaxConcurrentRuns: configuration.GetValue<int>("Scraping:MaxConcurrentRuns", 3),
    MaxConcurrentDbWrites: configuration.GetValue<int>("Scraping:MaxConcurrentDbWrites", 2),
    MaxConcurrentSearches: configuration.GetValue<int>("Scraping:MaxConcurrentSearches", 5),
    MaxConcurrentDescriptionFetches: configuration.GetValue<int>("Scraping:MaxConcurrentDescriptionFetches", 10),
    EmbeddingBatchSize: configuration.GetValue<int>("Scraping:EmbeddingBatchSize", 50));
builder.Services.AddSingleton(scrapingConfig);
builder.Services.AddSingleton(new DbWriteGate(scrapingConfig.MaxConcurrentDbWrites));

builder.Services.AddSingleton<IListingIndexingService, NullListingIndexingService>();
builder.Services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();
builder.Services.AddSingleton<SearchBatchStage>();
builder.Services.AddSingleton<IBatchPipelineRunner, BatchPipelineRunner>();
builder.Services.AddHostedService<StartupRecoveryService>();
builder.Services.AddHostedService<NightlyScrapeService>();

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new HealthResponse("healthy")));
app.MapGet("/health", () => Results.Ok(new HealthResponse("healthy")));

app.MapJobEndpoints();
app.MapCategoryEndpoints();
app.MapHistoryEndpoints();
app.MapBatchHistoryEndpoints();
app.MapListingEndpoints();
app.MapScrapeEndpoints();
app.MapOverviewEndpoints();
app.MapMarketsEndpoints();
app.MapChatEndpoints();
app.MapOpportunityEndpoints();

app.Run();

public record HealthResponse(string Status);
