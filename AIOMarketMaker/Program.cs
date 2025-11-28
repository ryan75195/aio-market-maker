// Program.cs
using AIOMarketMaker.Services;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// Database connection
var dbPath = builder.Configuration["DatabasePath"] ?? "etl.db";
var connectionString = $"Data Source={dbPath}";
builder.Services.AddDbContext<EtlDbContext>(options =>
    options.UseSqlite(connectionString));

// Ebay scraper services
builder.Services.AddEbayScraperPipeline(builder.Configuration);

// Job runner for ETL operations
builder.Services.AddScoped<IJobRunner, JobRunner>();

// Status refresh runner for checking listing status changes
builder.Services.AddScoped<IStatusRefreshRunner, StatusRefreshRunner>();

var host = builder.Build();
host.Run();