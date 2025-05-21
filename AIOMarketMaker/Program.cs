// Program.cs
using AIOMarketMaker.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();

// 👇 Use builder.Configuration directly
builder.Services.AddEbayScraperPipeline(builder.Configuration);

var host = builder.Build();
host.Run();