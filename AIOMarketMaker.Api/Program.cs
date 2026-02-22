using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Migrations;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.ML.Services;
using AIOMarketMaker.Core.Parsers;
using ScraperWorker.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using Serilog;
using Serilog.Formatting.Compact;

// Configure Serilog with optional file sink
var logSessionPath = Environment.GetEnvironmentVariable("LOG_SESSION_PATH");
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

// Clustering service
var clusteringConfig = new ClusteringConfig(
    configuration.GetValue<int>("Clustering:MinClusterSize", 8),
    configuration.GetValue<int>("Clustering:MinPoints", 4));
builder.Services.AddSingleton(clusteringConfig);
builder.Services.AddSingleton<IClusteringService, ClusteringService>();

// Vector index (local USearch)
var vectorIndexConfig = new VectorIndexConfig(
    IndexPath: configuration.GetValue<string>("VectorIndex:IndexPath") ?? "./data/vectors.usearch",
    IdMapPath: configuration.GetValue<string>("VectorIndex:IdMapPath") ?? "./data/vectors-idmap.json",
    TopK: configuration.GetValue<int>("VectorIndex:TopK", 30),
    SimilarityThreshold: configuration.GetValue<float>("VectorIndex:SimilarityThreshold", 0.80f),
    Dimensions: configuration.GetValue<int>("VectorIndex:Dimensions", 3072),
    Connectivity: configuration.GetValue<int>("VectorIndex:Connectivity", 16),
    ExpansionAdd: configuration.GetValue<int>("VectorIndex:ExpansionAdd", 128),
    ExpansionSearch: configuration.GetValue<int>("VectorIndex:ExpansionSearch", 64));
builder.Services.AddSingleton(vectorIndexConfig);
builder.Services.AddSingleton<IVectorIndex>(sp =>
{
    var config = sp.GetRequiredService<VectorIndexConfig>();
    var index = new USearchVectorIndex(config);
    if (File.Exists(config.IndexPath) && File.Exists(config.IdMapPath))
    {
        index.Load();
        var logger = sp.GetRequiredService<ILogger<USearchVectorIndex>>();
        logger.LogInformation("Loaded vector index with {Count} vectors from {Path}",
            index.Count, config.IndexPath);
    }
    return index;
});
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();

// Listing indexing service
builder.Services.AddSingleton<IListingIndexingService, ListingIndexingService>();

// Pricing analysis service
builder.Services.AddSingleton<IPricingAnalysisService, PricingAnalysisService>();

// Variant classifier (local ONNX model)
var classifierConfig = new OnnxClassifierConfig(
    ModelPath: configuration.GetValue<string>("VariantClassifier:ModelPath") ?? "models/variant-classifier/model.onnx",
    VocabPath: configuration.GetValue<string>("VariantClassifier:VocabPath") ?? "models/variant-classifier/vocab.json",
    MergesPath: configuration.GetValue<string>("VariantClassifier:MergesPath") ?? "models/variant-classifier/merges.txt",
    MaxLength: configuration.GetValue<int?>("VariantClassifier:MaxLength") ?? 256);
builder.Services.AddSingleton(classifierConfig);
builder.Services.AddSingleton<VariantModelRunner>();

// ComparablesEtlService
builder.Services.AddScoped<IComparablesEtlService, ComparablesEtlService>();
builder.Services.AddScoped<IListingPredictionService, ListingPredictionService>();

// Scraping concurrency config
var scrapingConfig = new ScrapingConfig(
    MaxConcurrentRuns: configuration.GetValue<int>("Scraping:MaxConcurrentRuns", 3),
    MaxConcurrentDbWrites: configuration.GetValue<int>("Scraping:MaxConcurrentDbWrites", 2));
builder.Services.AddSingleton(scrapingConfig);
builder.Services.AddSingleton(new DbWriteGate(scrapingConfig.MaxConcurrentDbWrites));

builder.Services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();
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

app.Run();

public record HealthResponse(string Status);
