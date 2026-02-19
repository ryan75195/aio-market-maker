using AIOMarketMaker.Tests.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.E2E.Infrastructure;

public class MockEbayServer : IDisposable
{
    private WebApplication? _app;
    private readonly int _port;
    private readonly string _dataDirectory;
    private Task? _runTask;

    // Map listing IDs to HTML filenames
    private static readonly Dictionary<string, string> ListingFiles = new()
    {
        { "306278488042", "ActiveBuyItNowListing.htm" },
        { "256918168190", "SoldBuyNowListing.htm" },
    };

    public MockEbayServer(int port = 9999)
    {
        _port = port;
        _dataDirectory = TestDataPaths.Root;
    }

    public string BaseUrl => $"http://localhost:{_port}";

    public void Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // Route: /itm/{id} -> Listings/*.htm
        _app.MapGet("/itm/{id}", (string id) => ServeListingHtml(id));

        // Route: /sch/i.html -> Search/*.htm
        _app.MapGet("/sch/i.html", (HttpContext ctx) => ServeSearchHtml(ctx.Request.Query));

        _runTask = _app.RunAsync($"http://localhost:{_port}");
    }

    private IResult ServeListingHtml(string id)
    {
        if (ListingFiles.TryGetValue(id, out var filename))
        {
            var filePath = Path.Combine(_dataDirectory, "Listings", filename);
            if (File.Exists(filePath))
            {
                var html = File.ReadAllText(filePath);
                return Results.Content(html, "text/html");
            }
        }
        return Results.NotFound($"No mock HTML for listing {id}");
    }

    private IResult ServeSearchHtml(IQueryCollection query)
    {
        var isSold = query.ContainsKey("LH_Sold");
        var filename = isSold
            ? "Sold_With_Small_Number_of_Real_Results.htm"
            : "SearchResultsContainingPriceRanges.htm";

        var filePath = Path.Combine(_dataDirectory, "Search", filename);
        if (File.Exists(filePath))
        {
            var html = File.ReadAllText(filePath);
            return Results.Content(html, "text/html");
        }
        return Results.NotFound($"No mock HTML for search");
    }

    public void Dispose()
    {
        if (_app != null)
        {
            _app.StopAsync().Wait(TimeSpan.FromSeconds(5));
            _app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }
}
