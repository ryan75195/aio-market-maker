// Program.cs
using AIOMarketMaker.Services;
using AIOMarketMaker.Etl.Configuration;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Migrations;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Services.EntityResolution;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Database connection
var dbPath = builder.Configuration["DatabasePath"] ?? "etl.db";
var connectionString = $"Data Source={dbPath}";

// Run database migrations on startup
var migrationRunner = new MigrationRunner(connectionString, null);
migrationRunner.ApplyMigrations();

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
builder.Services.AddSingleton<IEntityResolutionService, OpenAiEntityResolutionService>();

// Job runner for ETL operations
builder.Services.AddScoped<IJobRunner, JobRunner>();

// Status refresh runner for checking listing status changes
builder.Services.AddScoped<IStatusRefreshRunner, StatusRefreshRunner>();

var host = builder.Build();
host.Run();