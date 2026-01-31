using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Data.SqlClient;
using AIOMarketMaker.Etl.Endpoints;

namespace AIOMarketMaker.Tests.Integration;

/// <summary>
/// Integration tests for the simplified pipeline (without Durable Functions).
///
/// REQUIREMENTS:
/// These tests require the following infrastructure to be running:
///
/// 1. Azurite (Azure Storage Emulator)
///    - Port 10000: Blob Storage
///    - Port 10001: Queue Storage
///    - Port 10002: Table Storage
///    Start with: npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002
///
/// 2. SQL Server LocalDB
///    - Server: (localdb)\MSSQLLocalDB
///    - Database: AIOMarketMaker
///    - Migrations must be applied
///
/// 3. ScraperWorker (for full flow tests)
///    - Run from: AIOWebScraper/ScraperWorker
///    - Command: dotnet run -- --dedicated-mode
///    - Port: 7126 (via Azure Functions proxy) or 5000 (direct)
///
/// 4. ETL Functions Host (for endpoint tests)
///    - Run from: AIOMarketMaker/AIOMarketMaker.Etl
///    - Command: func start
///    - Port: 7072 (or configured port)
///
/// These tests are marked [Explicit] and should be run manually during local development.
/// They verify the entire pipeline works end-to-end without Durable Functions.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Requires local infrastructure running: Azurite, LocalDB, ScraperWorker, ETL Functions host")]
public class SimplifiedPipeline_IntegrationTests
{
    private const string LocalDbConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;";

    private const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

    // The ETL Functions host URL when running locally
    private const string EtlFunctionsBaseUrl = "http://localhost:7072";

    private HttpClient _httpClient = null!;
    private BlobServiceClient _blobService = null!;
    private QueueServiceClient _queueService = null!;

    // Track test data for cleanup
    private readonly List<int> _createdScrapeRunIds = new();
    private readonly List<string> _createdListingIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(EtlFunctionsBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };

        _blobService = new BlobServiceClient(AzuriteConnectionString);
        _queueService = new QueueServiceClient(AzuriteConnectionString);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _httpClient?.Dispose();
    }

    [TearDown]
    public async Task TearDown()
    {
        // Clean up test data from database
        await CleanupTestDataAsync();
    }

    /// <summary>
    /// Tests the full pipeline flow from trigger to completion:
    /// 1. Creates a test ScrapeJob
    /// 2. Triggers SimplifiedScrapeTrigger via HTTP
    /// 3. Waits for ScraperWorker to process queue messages
    /// 4. Waits for CompletionCheckTrigger to mark run as completed
    /// 5. Verifies ScrapeRun.Status == "Completed"
    /// 6. Verifies Listings exist in database
    /// 7. Verifies all ScrapeRunListings marked Complete
    ///
    /// IMPORTANT: This test requires:
    /// - A real eBay search term that will return results
    /// - ScraperWorker running to process listing pages
    /// - CompletionCheckTrigger running to mark completion
    /// - The full pipeline may take 2-5 minutes to complete
    /// </summary>
    [Test]
    [Timeout(300000)] // 5 minute timeout
    public async Task Full_flow_should_complete_without_durable_functions()
    {
        // Arrange - Create a test job with a known search term
        var testJobId = await CreateTestScrapeJobAsync("PS5 Console");

        try
        {
            // Act - Trigger manual scrape via HTTP
            var triggerResponse = await _httpClient.PostAsJsonAsync(
                "/api/scrape/start",
                new { jobId = testJobId });

            Assert.That(triggerResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "Manual scrape trigger should succeed");

            var triggerResult = await triggerResponse.Content.ReadFromJsonAsync<ManualScrapeResponse>();
            Assert.That(triggerResult, Is.Not.Null);
            Assert.That(triggerResult!.Results, Is.Not.Empty, "Should have at least one job result");

            var jobResult = triggerResult.Results.First();
            Assert.That(jobResult.Success, Is.True, $"Job trigger should succeed: {jobResult.Error}");
            Assert.That(jobResult.ListingsFound, Is.GreaterThan(0),
                "Should find some listings from eBay search");

            // Get the ScrapeRun ID for this job
            var scrapeRunId = await GetLatestScrapeRunIdAsync(testJobId);
            Assert.That(scrapeRunId, Is.GreaterThan(0), "ScrapeRun should be created");
            _createdScrapeRunIds.Add(scrapeRunId);

            // Wait for completion (CompletionCheckTrigger runs every 30 seconds)
            // Poll every 10 seconds for up to 5 minutes
            var completedStatus = await WaitForScrapeRunCompletionAsync(scrapeRunId, TimeSpan.FromMinutes(5));

            // Assert - ScrapeRun should be completed
            Assert.That(completedStatus, Is.EqualTo("Completed"),
                "ScrapeRun should reach Completed status");

            // Assert - Listings should exist in database
            var listingsCount = await GetListingsCountForJobAsync(testJobId);
            Assert.That(listingsCount, Is.GreaterThan(0),
                "Listings should be created in database");

            // Assert - All ScrapeRunListings should be Complete
            var pendingCount = await GetPendingScrapeRunListingsCountAsync(scrapeRunId);
            Assert.That(pendingCount, Is.EqualTo(0),
                "All ScrapeRunListings should be marked Complete");

            // Log summary for debugging
            TestContext.WriteLine($"Full flow completed successfully:");
            TestContext.WriteLine($"  - ScrapeRun ID: {scrapeRunId}");
            TestContext.WriteLine($"  - Listings created: {listingsCount}");
        }
        finally
        {
            // Cleanup will happen in TearDown
            await DeleteTestScrapeJobAsync(testJobId);
        }
    }

    /// <summary>
    /// Tests ProcessListingEndpoint directly:
    /// 1. Upload test HTML to blob storage
    /// 2. Create required database records (ScrapeRun, ScrapeRunListing)
    /// 3. Call ProcessListing endpoint with correct request
    /// 4. Verify listing is created/updated in database
    /// 5. Verify ScrapeRunListing is marked Complete
    ///
    /// This test isolates the processing logic from the scraping infrastructure.
    /// </summary>
    [Test]
    public async Task ProcessListingEndpoint_should_process_blob_and_update_database()
    {
        // Arrange - Create test data in database
        var testJobId = await CreateTestScrapeJobAsync("Test Integration Job");
        var testScrapeRunId = await CreateTestScrapeRunAsync(testJobId);
        _createdScrapeRunIds.Add(testScrapeRunId);

        const string testListingId = "999888777666";
        var testScrapeRunListingId = await CreateTestScrapeRunListingAsync(
            testScrapeRunId, testJobId, testListingId);

        // Upload test HTML to blob storage
        // This is a minimal valid eBay listing HTML structure
        var testHtml = GenerateValidListingHtml(testListingId, "Integration Test Product", 149.99m);
        var blobPath = $"{testScrapeRunId}/{testListingId}/listing.html";
        await UploadTestBlobAsync(blobPath, testHtml);

        try
        {
            // Act - Call ProcessListing endpoint
            var request = new ProcessListingRequest(
                ScrapeRunId: testScrapeRunId,
                ScrapeRunListingId: testScrapeRunListingId,
                ListingId: testListingId,
                ScrapeJobId: testJobId,
                BlobPath: blobPath);

            var response = await _httpClient.PostAsJsonAsync("/api/process-listing", request);

            // Assert - Response should indicate success
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var result = await response.Content.ReadFromJsonAsync<ProcessListingResponse>();
            Assert.That(result, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(result!.Success, Is.True, $"Processing should succeed: {result.ErrorMessage}");
                Assert.That(result.Status, Is.EqualTo("added").Or.EqualTo("updated"),
                    "Status should be 'added' or 'updated'");
            });

            // Assert - Listing should exist in database
            var listing = await GetListingByIdAsync(testListingId, testJobId);
            Assert.That(listing, Is.Not.Null, "Listing should be created in database");
            Assert.Multiple(() =>
            {
                Assert.That(listing!.Title, Is.EqualTo("Integration Test Product"));
                Assert.That(listing.Price, Is.EqualTo(149.99m));
            });

            // Assert - ScrapeRunListing should be Complete
            var srlStatus = await GetScrapeRunListingStatusAsync(testScrapeRunId, testListingId);
            Assert.That(srlStatus, Is.EqualTo("Complete"),
                "ScrapeRunListing should be marked Complete");

            // Track for cleanup
            _createdListingIds.Add(testListingId);
        }
        finally
        {
            // Cleanup blob
            await DeleteTestBlobAsync(blobPath);
            await DeleteTestScrapeJobAsync(testJobId);
        }
    }

    /// <summary>
    /// Tests that ProcessListingEndpoint correctly handles idempotent requests
    /// (processing the same listing twice should return "skipped" on second call).
    /// </summary>
    [Test]
    public async Task ProcessListingEndpoint_should_skip_already_processed_listing()
    {
        // Arrange - Create test data
        var testJobId = await CreateTestScrapeJobAsync("Test Idempotency Job");
        var testScrapeRunId = await CreateTestScrapeRunAsync(testJobId);
        _createdScrapeRunIds.Add(testScrapeRunId);

        const string testListingId = "111222333444";
        var testScrapeRunListingId = await CreateTestScrapeRunListingAsync(
            testScrapeRunId, testJobId, testListingId);

        var testHtml = GenerateValidListingHtml(testListingId, "Idempotency Test", 99.99m);
        var blobPath = $"{testScrapeRunId}/{testListingId}/listing.html";
        await UploadTestBlobAsync(blobPath, testHtml);

        try
        {
            var request = new ProcessListingRequest(
                ScrapeRunId: testScrapeRunId,
                ScrapeRunListingId: testScrapeRunListingId,
                ListingId: testListingId,
                ScrapeJobId: testJobId,
                BlobPath: blobPath);

            // Act - First call should process
            var firstResponse = await _httpClient.PostAsJsonAsync("/api/process-listing", request);
            var firstResult = await firstResponse.Content.ReadFromJsonAsync<ProcessListingResponse>();

            Assert.That(firstResult!.Success, Is.True, "First call should succeed");
            Assert.That(firstResult.Status, Is.EqualTo("added"), "First call should add listing");

            // Act - Second call should skip
            var secondResponse = await _httpClient.PostAsJsonAsync("/api/process-listing", request);
            var secondResult = await secondResponse.Content.ReadFromJsonAsync<ProcessListingResponse>();

            // Assert
            Assert.Multiple(() =>
            {
                Assert.That(secondResult!.Success, Is.True, "Second call should succeed");
                Assert.That(secondResult.Status, Is.EqualTo("skipped"),
                    "Second call should skip already-processed listing");
            });

            _createdListingIds.Add(testListingId);
        }
        finally
        {
            await DeleteTestBlobAsync(blobPath);
            await DeleteTestScrapeJobAsync(testJobId);
        }
    }

    #region Helper Methods

    private async Task<int> CreateTestScrapeJobAsync(string searchTerm)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "INSERT INTO ScrapeJobs (SearchTerm, IsEnabled, CreatedUtc) OUTPUT INSERTED.Id VALUES (@SearchTerm, 1, GETUTCDATE())",
            connection);
        command.Parameters.AddWithValue("@SearchTerm", searchTerm);

        var result = await command.ExecuteScalarAsync();
        return result is int id ? id : throw new InvalidOperationException("Failed to create ScrapeJob");
    }

    private async Task DeleteTestScrapeJobAsync(int jobId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "DELETE FROM ScrapeJobs WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@Id", jobId);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CreateTestScrapeRunAsync(int jobId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            @"INSERT INTO ScrapeRuns (JobId, Status, TriggerType, StartedUtc, InstanceId, TotalListingsFound, ListingsProcessed)
              OUTPUT INSERTED.Id
              VALUES (@JobId, 'Indexing', 'Manual', GETUTCDATE(), NEWID(), 0, 0)",
            connection);
        command.Parameters.AddWithValue("@JobId", jobId);

        var result = await command.ExecuteScalarAsync();
        return result is int id ? id : throw new InvalidOperationException("Failed to create ScrapeRun");
    }

    private async Task<int> CreateTestScrapeRunListingAsync(int scrapeRunId, int scrapeJobId, string listingId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            @"INSERT INTO ScrapeRunListings (ScrapeRunId, ScrapeJobId, ListingId, Status, CreatedUtc)
              OUTPUT INSERTED.Id
              VALUES (@ScrapeRunId, @ScrapeJobId, @ListingId, 'Pending', GETUTCDATE())",
            connection);
        command.Parameters.AddWithValue("@ScrapeRunId", scrapeRunId);
        command.Parameters.AddWithValue("@ScrapeJobId", scrapeJobId);
        command.Parameters.AddWithValue("@ListingId", listingId);

        var result = await command.ExecuteScalarAsync();
        return result is int id ? id : throw new InvalidOperationException("Failed to create ScrapeRunListing");
    }

    private async Task<int> GetLatestScrapeRunIdAsync(int jobId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT TOP 1 Id FROM ScrapeRuns WHERE JobId = @JobId ORDER BY Id DESC",
            connection);
        command.Parameters.AddWithValue("@JobId", jobId);

        var result = await command.ExecuteScalarAsync();
        return result != null ? (int)result : 0;
    }

    private async Task<string> WaitForScrapeRunCompletionAsync(int scrapeRunId, TimeSpan timeout)
    {
        var endTime = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < endTime)
        {
            var status = await GetScrapeRunStatusAsync(scrapeRunId);

            if (status == "Completed" || status == "Failed")
            {
                return status;
            }

            TestContext.WriteLine($"ScrapeRun {scrapeRunId} status: {status}, waiting...");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        return "Timeout";
    }

    private async Task<string> GetScrapeRunStatusAsync(int scrapeRunId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT Status FROM ScrapeRuns WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@Id", scrapeRunId);

        var result = await command.ExecuteScalarAsync();
        return result?.ToString() ?? "NotFound";
    }

    private async Task<int> GetListingsCountForJobAsync(int jobId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM Listings WHERE ScrapeJobId = @JobId", connection);
        command.Parameters.AddWithValue("@JobId", jobId);

        var result = await command.ExecuteScalarAsync();
        return result is int count ? count : 0;
    }

    private async Task<int> GetPendingScrapeRunListingsCountAsync(int scrapeRunId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM ScrapeRunListings WHERE ScrapeRunId = @ScrapeRunId AND Status != 'Complete'",
            connection);
        command.Parameters.AddWithValue("@ScrapeRunId", scrapeRunId);

        var result = await command.ExecuteScalarAsync();
        return result is int count ? count : 0;
    }

    private async Task<ListingDto?> GetListingByIdAsync(string listingId, int scrapeJobId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT ListingId, Title, Price FROM Listings WHERE ListingId = @ListingId AND ScrapeJobId = @ScrapeJobId",
            connection);
        command.Parameters.AddWithValue("@ListingId", listingId);
        command.Parameters.AddWithValue("@ScrapeJobId", scrapeJobId);

        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new ListingDto(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2));
        }

        return null;
    }

    private async Task<string?> GetScrapeRunListingStatusAsync(int scrapeRunId, string listingId)
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT Status FROM ScrapeRunListings WHERE ScrapeRunId = @ScrapeRunId AND ListingId = @ListingId",
            connection);
        command.Parameters.AddWithValue("@ScrapeRunId", scrapeRunId);
        command.Parameters.AddWithValue("@ListingId", listingId);

        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    private async Task UploadTestBlobAsync(string blobPath, string content)
    {
        var container = _blobService.GetBlobContainerClient("html");
        await container.CreateIfNotExistsAsync();

        var blob = container.GetBlobClient(blobPath);
        await blob.UploadAsync(BinaryData.FromString(content), overwrite: true);
    }

    private async Task DeleteTestBlobAsync(string blobPath)
    {
        var container = _blobService.GetBlobContainerClient("html");
        var blob = container.GetBlobClient(blobPath);
        await blob.DeleteIfExistsAsync();
    }

    private static string GenerateValidListingHtml(string listingId, string title, decimal price)
    {
        // Generate HTML that is > 100KB (to pass error page detection)
        // and contains the elements the parser looks for
        var padding = new string('x', 100 * 1024); // 100KB padding

        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>{title} | eBay</title>
    <link rel=""canonical"" href=""https://www.ebay.co.uk/itm/{listingId}"" />
</head>
<body>
    <div class=""x-item-title"">
        <span class=""ux-textspans ux-textspans--BOLD"">{title}</span>
    </div>
    <div class=""x-price-primary"">
        <span class=""ux-textspans"">GBP {price:F2}</span>
    </div>
    <div class=""ux-labels-values__values-content"">
        <span class=""ux-textspans"">New</span>
    </div>
    <div class=""ux-action"">
        <span>Buy it now</span>
    </div>
    <div class=""ux-image-carousel"">
        <img src=""https://i.ebayimg.com/images/test/s-l500.jpg"" />
    </div>
    <span class=""ux-textspans ux-textspans--SECONDARY"">London, United Kingdom</span>
    <!-- Padding to make HTML > 100KB -->
    <div style=""display:none"">{padding}</div>
</body>
</html>";
    }

    private async Task CleanupTestDataAsync()
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        // Delete ScrapeRunListings for test runs
        foreach (var runId in _createdScrapeRunIds)
        {
            await using var srlCommand = new SqlCommand(
                "DELETE FROM ScrapeRunListings WHERE ScrapeRunId = @Id", connection);
            srlCommand.Parameters.AddWithValue("@Id", runId);
            await srlCommand.ExecuteNonQueryAsync();
        }

        // Delete ScrapeRuns
        foreach (var runId in _createdScrapeRunIds)
        {
            await using var srCommand = new SqlCommand(
                "DELETE FROM ScrapeRuns WHERE Id = @Id", connection);
            srCommand.Parameters.AddWithValue("@Id", runId);
            await srCommand.ExecuteNonQueryAsync();
        }

        // Delete test Listings
        foreach (var listingId in _createdListingIds)
        {
            await using var listingCommand = new SqlCommand(
                "DELETE FROM Listings WHERE ListingId = @ListingId", connection);
            listingCommand.Parameters.AddWithValue("@ListingId", listingId);
            await listingCommand.ExecuteNonQueryAsync();
        }

        _createdScrapeRunIds.Clear();
        _createdListingIds.Clear();
    }

    #endregion

    #region Response DTOs

    private record ManualScrapeResponse(IEnumerable<JobResult> Results);

    private record JobResult(
        int JobId,
        string SearchTerm,
        bool Success,
        int ListingsFound,
        string? Error);

    private record ListingDto(string ListingId, string? Title, decimal? Price);

    #endregion
}
