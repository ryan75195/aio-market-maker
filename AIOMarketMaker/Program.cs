// Program.cs
using AIOMarketMaker.Services;
using AIOMarketMaker.Etl.Configuration;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Migrations;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Services.EntityResolution;
using AIOMarketMaker.Etl.Services.VectorSearch;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using Serilog;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Database connection
var dbPath = builder.Configuration["DatabasePath"] ?? "etl.db";
var connectionString = $"Data Source={dbPath}";

// Run database migrations on startup
var migrationRunner = new MigrationRunner(connectionString, null);
migrationRunner.ApplyMigrations();

// Enable WAL mode for concurrent access from multiple processes
using (var connection = new SqliteConnection(connectionString))
{
    connection.Open();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "PRAGMA journal_mode=WAL;";
    var result = cmd.ExecuteScalar();
    Log.Information("SQLite journal mode set to: {Mode}", result);
}

builder.Services.AddDbContext<EtlDbContext>(options =>
    options.UseSqlite(connectionString));

// Ebay scraper services
builder.Services.AddEbayScraperPipeline(builder.Configuration);

// OpenAI Entity Resolution (required - throws if not configured)
var openAiSettings = builder.Configuration.GetSection("OpenAi").Get<OpenAiSettings>()
    ?? throw new InvalidOperationException("OpenAi configuration section is required in settings");

if (string.IsNullOrEmpty(openAiSettings.ApiKey))
    throw new InvalidOperationException("OpenAi:ApiKey is required");

builder.Services.AddSingleton(openAiSettings);
builder.Services.AddSingleton(new OpenAIClient(openAiSettings.ApiKey));
builder.Services.AddSingleton<PromptBuilder>();

// Pinecone vector search (optional - uses no-op if not configured)
var pineconeSettings = builder.Configuration.GetSection("Pinecone").Get<PineconeSettings>();
var embeddingSettings = builder.Configuration.GetSection("Embedding").Get<EmbeddingSettings>()
    ?? new EmbeddingSettings();

builder.Services.AddSingleton(embeddingSettings);

if (pineconeSettings != null && !string.IsNullOrEmpty(pineconeSettings.ApiKey))
{
    builder.Services.AddSingleton(pineconeSettings);
    builder.Services.AddSingleton<IEmbeddingService, OpenAiEmbeddingService>();
    builder.Services.AddSingleton<IPineconeService, PineconeService>();
    builder.Services.AddSingleton<IProductNameIndexer, ProductNameIndexer>();
    Log.Information("Pinecone vector search enabled");
}
else
{
    builder.Services.AddSingleton<IEmbeddingService, NoOpEmbeddingService>();
    builder.Services.AddSingleton<IPineconeService, NoOpPineconeService>();
    builder.Services.AddSingleton<IProductNameIndexer, NoOpProductNameIndexer>();
    Log.Warning("Pinecone not configured - vector search disabled");
}

builder.Services.AddSingleton<IEntityResolutionService, OpenAiEntityResolutionService>();

// Job runner for ETL operations
builder.Services.AddScoped<IJobRunner, JobRunner>();

// Status refresh runner for checking listing status changes
builder.Services.AddScoped<IStatusRefreshRunner, StatusRefreshRunner>();

var host = builder.Build();
host.Run();