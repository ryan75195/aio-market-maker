using System.Net;
using System.Net.Http.Json;
using NUnit.Framework;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;

namespace AIOMarketMaker.Tests.Integration;

/// <summary>
/// Integration tests for the inline ETL pipeline.
///
/// REQUIREMENTS:
/// These tests require the following infrastructure to be running:
///
/// 1. SQL Server LocalDB
///    - Server: (localdb)\MSSQLLocalDB
///    - Database: AIOMarketMaker
///    - Migrations must be applied
///
/// 2. ScraperWorker (for full flow tests)
///    - Run from: AIOWebScraper/ScraperWorker
///    - Command: dotnet run -- --dedicated-mode
///    - Port: 7126 (via Azure Functions proxy) or 5000 (direct)
///
/// 3. AIOMarketMaker.Api
///    - Run from: AIOMarketMaker.Api
///    - Command: dotnet run
///
/// These tests are marked [Explicit] and should be run manually during local development.
/// They verify the entire pipeline works end-to-end with inline description fetching.
/// </summary>
[TestFixture]
[Category("Integration")]
[Explicit("Requires local infrastructure running: LocalDB, ScraperWorker, API")]
public class SimplifiedPipeline_IntegrationTests
{
    private const string LocalDbConnectionString =
        "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;";

    // The API host URL when running locally
    private const string ApiBaseUrl = "http://localhost:5000";

    private HttpClient _httpClient = null!;

    // Track test data for cleanup
    private readonly List<int> _createdScrapeRunIds = new();
    private readonly List<string> _createdListingIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _httpClient?.Dispose();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestDataAsync();
    }

    /// <summary>
    /// Tests the full inline pipeline flow:
    /// 1. Creates a test ScrapeJob
    /// 2. Triggers scrape via API
    /// 3. Waits for inline processing to complete
    /// 4. Verifies ScrapeRun.Status == "Completed"
    /// 5. Verifies Listings exist in database
    /// </summary>
    [Test]
    [Timeout(300000)] // 5 minute timeout
    public async Task Full_flow_should_complete_with_inline_processing()
    {
        // Arrange - Create a test job with a known search term
        var testJobId = await CreateTestScrapeJobAsync("PS5 Console");

        try
        {
            // Act - Trigger manual scrape via API
            var triggerResponse = await _httpClient.PostAsJsonAsync(
                "/api/scrape/start",
                new { jobId = testJobId });

            Assert.That(triggerResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                "Manual scrape trigger should succeed");

            // Get the ScrapeRun ID for this job
            var scrapeRunId = await GetLatestScrapeRunIdAsync(testJobId);
            Assert.That(scrapeRunId, Is.GreaterThan(0), "ScrapeRun should be created");
            _createdScrapeRunIds.Add(scrapeRunId);

            // Wait for completion
            var completedStatus = await WaitForScrapeRunCompletionAsync(scrapeRunId, TimeSpan.FromMinutes(5));

            // Assert - ScrapeRun should be completed
            Assert.That(completedStatus, Is.EqualTo("Completed"),
                "ScrapeRun should reach Completed status");

            // Assert - Listings should exist in database
            var listingsCount = await GetListingsCountForJobAsync(testJobId);
            Assert.That(listingsCount, Is.GreaterThan(0),
                "Listings should be created in database");

            // Log summary for debugging
            TestContext.WriteLine($"Full flow completed successfully:");
            TestContext.WriteLine($"  - ScrapeRun ID: {scrapeRunId}");
            TestContext.WriteLine($"  - Listings created: {listingsCount}");
        }
        finally
        {
            await DeleteTestScrapeJobAsync(testJobId);
        }
    }

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

    private async Task CleanupTestDataAsync()
    {
        await using var connection = new SqlConnection(LocalDbConnectionString);
        await connection.OpenAsync();

        // Delete ScrapeRunIssues for test runs
        foreach (var runId in _createdScrapeRunIds)
        {
            await using var issueCommand = new SqlCommand(
                "DELETE FROM ScrapeRunIssues WHERE ScrapeRunId = @Id", connection);
            issueCommand.Parameters.AddWithValue("@Id", runId);
            await issueCommand.ExecuteNonQueryAsync();
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

    private record ListingDto(string ListingId, string? Title, decimal? Price);
}
