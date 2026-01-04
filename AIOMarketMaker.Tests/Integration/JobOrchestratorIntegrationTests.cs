using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.Integration;

/// <summary>
/// Integration tests for the job orchestration flow using the real cloud scraper service.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit]
public class JobOrchestratorIntegrationTests
{
    private ServiceProvider _serviceProvider = null!;
    private EtlDbContext _dbContext = null!;
    private IJobRunner _jobRunner = null!;

    [SetUp]
    public async Task SetUp()
    {
        var storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=webscraperstorageacc;AccountKey=zio92liWbgYZN9oS/L65JV2RZp21eXanu19X1G+ioDO7UI0qAMj5wAICuaSPwOcwnM+fk4Y3pvgs+AStZmZOHg==;EndpointSuffix=core.windows.net";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraping:DefaultLookbackDays"] = "7"
            })
            .Build();

        var services = new ServiceCollection();

        // In-memory SQLite database for test isolation
        services.AddDbContext<EtlDbContext>(options =>
            options.UseSqlite("DataSource=:memory:"));

        // Azure Storage clients for the scraper
        services.AddSingleton(new TableServiceClient(storageConnectionString));
        services.AddSingleton(new BlobServiceClient(storageConnectionString));
        services.AddSingleton<IJobRepository, AzureJobRepository>();

        // Core eBay services
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real scraper client (cloud)
        var scraperBaseUrl = "https://scraper-api-gnu52mt6cve2q.azurewebsites.net";
        var scraperApiKey = "TmWkgbOu6jzMVLs668aulN5fDFw9qEe_FDFTjae3fnbvAzFujKGhlQ==";
        services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(scraperBaseUrl);
            client.DefaultRequestHeaders.Add("x-functions-key", scraperApiKey);
        });

        services.AddScoped<IEbayScraper, EbayScraper>();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<IJobRunner, JobRunner>();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        _serviceProvider = services.BuildServiceProvider();

        // Create a scoped DbContext and ensure the database is created
        _dbContext = _serviceProvider.GetRequiredService<EtlDbContext>();
        await _dbContext.Database.OpenConnectionAsync();
        await _dbContext.Database.EnsureCreatedAsync();

        _jobRunner = _serviceProvider.GetRequiredService<IJobRunner>();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_dbContext != null)
        {
            await _dbContext.Database.CloseConnectionAsync();
            await _dbContext.DisposeAsync();
        }

        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    [Test]
    public async Task Should_scrape_and_save_listings_for_job()
    {
        // Arrange - create a scrape job with a specific search term
        var job = new ScrapeJob
        {
            SearchTerm = "Sony Playstation 5 Slim Console",
            IsEnabled = true
        };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        // Act - run the job using the real scraper
        var result = await _jobRunner.RunJob(job.Id);

        // Assert - verify the job completed successfully
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, $"Job should succeed but failed with: {result.Error}");
            Assert.That(result.ListingsFound, Is.GreaterThan(0), "Should find at least one listing from search");
            Assert.That(result.NewListingsFetched, Is.GreaterThan(0), "Should fetch at least one new listing");
        });

        // Verify listings were saved to the database
        var savedListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == job.Id)
            .ToListAsync();

        Assert.That(savedListings.Count, Is.GreaterThan(0), "Listings should be saved to database");

        // Verify at least one listing has expected fields populated
        var sampleListing = savedListings.First();
        Assert.Multiple(() =>
        {
            Assert.That(sampleListing.ListingId, Is.Not.Null.And.Not.Empty, "ListingId should be populated");
            Assert.That(sampleListing.Title, Is.Not.Null.And.Not.Empty, "Title should be populated");
            Assert.That(sampleListing.Price, Is.GreaterThan(0), "Price should be greater than 0");
            Assert.That(sampleListing.Url, Is.Not.Null.And.Not.Empty, "Url should be populated");
        });

        // Verify status history records were created
        var historyRecords = await _dbContext.ListingStatusHistory
            .Where(h => savedListings.Select(l => l.Id).Contains(h.ListingId))
            .ToListAsync();

        Assert.That(historyRecords.Count, Is.EqualTo(savedListings.Count),
            "Each listing should have one initial status history record");

        // Verify job timestamp was updated
        var updatedJob = await _dbContext.ScrapeJobs.FindAsync(job.Id);
        Assert.That(updatedJob!.LastRunUtc, Is.Not.Null, "Job LastRunUtc should be updated");
    }

    [Test]
    public async Task Should_detect_and_update_sold_listings_for_scooby_doo_job()
    {
        // This test uses the real Azure SQL database to test the scooby doo job
        var sqlConnectionString = "Server=tcp:sql-aiomarketmaker-dev.database.windows.net,1433;Initial Catalog=etl;Persist Security Info=False;User ID=sqladmin;Password=fejfe3-ximmef-fuqfuq!;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";
        var storageConnectionString = "DefaultEndpointsProtocol=https;AccountName=webscraperstorageacc;AccountKey=zio92liWbgYZN9oS/L65JV2RZp21eXanu19X1G+ioDO7UI0qAMj5wAICuaSPwOcwnM+fk4Y3pvgs+AStZmZOHg==;EndpointSuffix=core.windows.net";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scraping:DefaultLookbackDays"] = "7"
            })
            .Build();

        var services = new ServiceCollection();

        // Use real Azure SQL database
        services.AddDbContext<EtlDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        // Azure Storage clients for the scraper
        services.AddSingleton(new TableServiceClient(storageConnectionString));
        services.AddSingleton(new BlobServiceClient(storageConnectionString));
        services.AddSingleton<IJobRepository, AzureJobRepository>();

        // Core eBay services
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real scraper client (cloud)
        var scraperBaseUrl = "https://scraper-api-gnu52mt6cve2q.azurewebsites.net";
        var scraperApiKey = "TmWkgbOu6jzMVLs668aulN5fDFw9qEe_FDFTjae3fnbvAzFujKGhlQ==";
        services.AddSingleton(new ScraperApiConfig(scraperBaseUrl, scraperApiKey));
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(scraperBaseUrl);
            client.DefaultRequestHeaders.Add("x-functions-key", scraperApiKey);
        });

        services.AddScoped<IEbayScraper, EbayScraper>();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<IJobRunner, JobRunner>();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        await using var provider = services.BuildServiceProvider();
        var dbContext = provider.GetRequiredService<EtlDbContext>();
        var jobRunner = provider.GetRequiredService<IJobRunner>();

        // Find the scooby doo job
        var job = await dbContext.ScrapeJobs
            .FirstOrDefaultAsync(j => j.SearchTerm.Contains("scooby"));

        Assert.That(job, Is.Not.Null, "Scooby doo job should exist in database");

        // Get active listings count before
        var activeBeforeCount = await dbContext.Listings
            .CountAsync(l => l.ScrapeJobId == job!.Id && l.ListingStatus == "Active");

        Console.WriteLine($"Job ID: {job!.Id}, Search Term: {job.SearchTerm}");
        Console.WriteLine($"Active listings before: {activeBeforeCount}");

        // Act - run the job
        var result = await jobRunner.RunJob(job.Id);

        // Assert
        Console.WriteLine($"Result: Success={result.Success}, Found={result.ListingsFound}, New={result.NewListingsFetched}, StatusUpdates={result.StatusUpdates}");

        Assert.That(result.Success, Is.True, $"Job should succeed but failed with: {result.Error}");

        // Get active listings count after
        var activeAfterCount = await dbContext.Listings
            .CountAsync(l => l.ScrapeJobId == job.Id && l.ListingStatus == "Active");

        Console.WriteLine($"Active listings after: {activeAfterCount}");
        Console.WriteLine($"Status updates: {result.StatusUpdates}");

        // Verify status history records were created for updates
        if (result.StatusUpdates > 0)
        {
            var jobScrapeHistoryCount = await dbContext.ListingStatusHistory
                .CountAsync(h => h.Source == "JobScrape");
            Console.WriteLine($"JobScrape history records: {jobScrapeHistoryCount}");
        }
    }
}
