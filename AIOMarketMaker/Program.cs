// Program.cs
using AIOMarketMaker.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// ← instead of inlining all the registrations, just:
builder.Services.AddEbayScraperPipeline();

var host = builder.Build();
host.Run();
