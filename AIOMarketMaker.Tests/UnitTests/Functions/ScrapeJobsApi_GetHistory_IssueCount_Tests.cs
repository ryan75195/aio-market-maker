using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Functions;
using AIOMarketMaker.Tests.Utils;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Tests.UnitTests.Functions;

[TestFixture]
[Category("Unit")]
public class ScrapeJobsApi_GetHistory_IssueCount_Tests
{
    private EtlDbContext _dbContext = null!;
    private ScrapeJobsApi _api = null!;
    private Mock<FunctionContext> _mockFunctionContext = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _api = new ScrapeJobsApi(_dbContext, NullLogger<ScrapeJobsApi>.Instance);

        // Setup service provider with JSON serializer options
        var services = new ServiceCollection();
        services.Configure<Microsoft.Azure.Functions.Worker.WorkerOptions>(options =>
        {
            options.Serializer = new Azure.Core.Serialization.JsonObjectSerializer(
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        });
        _serviceProvider = services.BuildServiceProvider();

        _mockFunctionContext = new Mock<FunctionContext>();
        _mockFunctionContext.Setup(c => c.InstanceServices).Returns(_serviceProvider);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    private HttpRequestData CreateMockRequest()
    {
        var mockRequest = new Mock<HttpRequestData>(_mockFunctionContext.Object);

        mockRequest.Setup(r => r.CreateResponse()).Returns(() =>
        {
            var mockResponse = new Mock<HttpResponseData>(_mockFunctionContext.Object);
            mockResponse.SetupProperty(r => r.StatusCode, HttpStatusCode.OK);
            mockResponse.SetupProperty(r => r.Body, new MemoryStream());
            var headers = new HttpHeadersCollection();
            mockResponse.SetupGet(r => r.Headers).Returns(headers);
            mockResponse.SetupSet(r => r.Headers = It.IsAny<HttpHeadersCollection>());
            return mockResponse.Object;
        });

        return mockRequest.Object;
    }

    private async Task<ScrapeRun> SeedScrapeRun(string status = "Running")
    {
        var job = new ScrapeJob { SearchTerm = "test-search", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        var run = new ScrapeRun
        {
            InstanceId = "test-instance",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = status,
            CurrentPhase = "Processing",
            JobId = job.Id
        };
        _dbContext.ScrapeRuns.Add(run);
        await _dbContext.SaveChangesAsync();

        return run;
    }

    [Test]
    public async Task Should_include_retrying_listings_in_IssueCount()
    {
        // Arrange - Create a run with 1 failed issue and 2 retrying listings
        var run = await SeedScrapeRun();

        // Add 2 retrying listings (ParseAttempts > 0, Status not Complete/Failed)
        _dbContext.ScrapeRunListings.AddRange(
            new ScrapeRunListing
            {
                ScrapeRunId = run.Id,
                ScrapeJobId = run.JobId!.Value,
                ListingId = "111111111",
                Status = "Pending",
                ParseAttempts = 1,
                FailureDetails = "Missing: price",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunListing
            {
                ScrapeRunId = run.Id,
                ScrapeJobId = run.JobId!.Value,
                ListingId = "222222222",
                Status = "Pending",
                ParseAttempts = 2,
                FailureDetails = "Missing: title",
                CreatedUtc = DateTime.UtcNow
            }
        );

        // Add 1 failed issue
        var failedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "333333333",
            Status = "Failed",
            ParseAttempts = 3,
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(failedListing);

        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = run.Id,
            ListingId = "333333333",
            IssueType = "ParseFailure",
            ErrorMessage = "Parse retries exhausted",
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun = runs!.FirstOrDefault(r => r.Id == run.Id);
        Assert.That(resultRun, Is.Not.Null);
        // IssueCount should be 3: 2 retrying + 1 failed
        Assert.That(resultRun!.IssueCount, Is.EqualTo(3), "IssueCount should include both retrying listings (2) and failed issues (1)");
    }

    [Test]
    public async Task Should_return_zero_IssueCount_when_no_retrying_or_failed_listings()
    {
        // Arrange - Create a run with only completed listings (no retries)
        var run = await SeedScrapeRun();

        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "444444444",
            Status = "Complete",
            ParseAttempts = 0,
            CreatedUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun = runs!.FirstOrDefault(r => r.Id == run.Id);
        Assert.That(resultRun!.IssueCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Should_only_count_retrying_listings_with_ParseAttempts_greater_than_zero()
    {
        // Arrange - Listings with ParseAttempts = 0 should not be counted as retrying
        var run = await SeedScrapeRun();

        // ParseAttempts = 0 (never retried, still pending)
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "555555555",
            Status = "Pending",
            ParseAttempts = 0,
            CreatedUtc = DateTime.UtcNow
        });

        // ParseAttempts = 1 (retrying)
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "666666666",
            Status = "Pending",
            ParseAttempts = 1,
            FailureDetails = "Missing: price",
            CreatedUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun = runs!.FirstOrDefault(r => r.Id == run.Id);
        Assert.That(resultRun!.IssueCount, Is.EqualTo(1), "Only listings with ParseAttempts > 0 should be counted as retrying");
    }

    [Test]
    public async Task Should_not_count_completed_listings_as_retrying()
    {
        // Arrange - Listings with Status = "Complete" should not be counted even with ParseAttempts > 0
        var run = await SeedScrapeRun();

        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "777777777",
            Status = "Complete",
            ParseAttempts = 2,  // Had retries but succeeded
            CreatedUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun = runs!.FirstOrDefault(r => r.Id == run.Id);
        Assert.That(resultRun!.IssueCount, Is.EqualTo(0), "Completed listings should not be counted as issues");
    }

    [Test]
    public async Task Should_not_count_failed_listings_as_retrying()
    {
        // Arrange - Listings with Status = "Failed" should not be double-counted
        // They are only counted via ScrapeRunIssues
        var run = await SeedScrapeRun();

        var failedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "888888888",
            Status = "Failed",
            ParseAttempts = 3,
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(failedListing);

        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = run.Id,
            ListingId = "888888888",
            IssueType = "ParseFailure",
            ErrorMessage = "Parse retries exhausted",
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun = runs!.FirstOrDefault(r => r.Id == run.Id);
        Assert.That(resultRun!.IssueCount, Is.EqualTo(1), "Failed listing should only be counted once (via ScrapeRunIssues)");
    }

    [Test]
    public async Task Should_correctly_calculate_IssueCount_for_multiple_runs()
    {
        // Arrange - Create two runs with different issue counts
        var run1 = await SeedScrapeRun();

        // Run 1: 1 retrying + 1 failed = 2 issues
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run1.Id,
            ScrapeJobId = run1.JobId!.Value,
            ListingId = "aaa111",
            Status = "Pending",
            ParseAttempts = 1,
            CreatedUtc = DateTime.UtcNow
        });
        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = run1.Id,
            ScrapeJobId = run1.JobId!.Value,
            ListingId = "aaa222",
            Status = "Failed",
            ParseAttempts = 3,
            CreatedUtc = DateTime.UtcNow
        });
        _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
        {
            ScrapeRunId = run1.Id,
            ListingId = "aaa222",
            IssueType = "ParseFailure",
            CreatedUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        // Create a second job and run
        var job2 = new ScrapeJob { SearchTerm = "test-search-2", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job2);
        await _dbContext.SaveChangesAsync();

        var run2 = new ScrapeRun
        {
            InstanceId = "test-instance-2",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow.AddMinutes(-3),
            Status = "Running",
            CurrentPhase = "Processing",
            JobId = job2.Id
        };
        _dbContext.ScrapeRuns.Add(run2);
        await _dbContext.SaveChangesAsync();

        // Run 2: 3 retrying = 3 issues
        _dbContext.ScrapeRunListings.AddRange(
            new ScrapeRunListing
            {
                ScrapeRunId = run2.Id,
                ScrapeJobId = job2.Id,
                ListingId = "bbb111",
                Status = "Pending",
                ParseAttempts = 1,
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunListing
            {
                ScrapeRunId = run2.Id,
                ScrapeJobId = job2.Id,
                ListingId = "bbb222",
                Status = "Pending",
                ParseAttempts = 2,
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunListing
            {
                ScrapeRunId = run2.Id,
                ScrapeJobId = job2.Id,
                ListingId = "bbb333",
                Status = "Pending",
                ParseAttempts = 1,
                CreatedUtc = DateTime.UtcNow
            }
        );
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistory(request);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var runs = JsonSerializer.Deserialize<List<HistoryRunDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var resultRun1 = runs!.FirstOrDefault(r => r.Id == run1.Id);
        var resultRun2 = runs!.FirstOrDefault(r => r.Id == run2.Id);

        Assert.Multiple(() =>
        {
            Assert.That(resultRun1!.IssueCount, Is.EqualTo(2), "Run 1 should have 2 issues (1 retrying + 1 failed)");
            Assert.That(resultRun2!.IssueCount, Is.EqualTo(3), "Run 2 should have 3 issues (3 retrying)");
        });
    }
}

/// <summary>
/// DTO for deserializing the GetHistory response in tests
/// </summary>
public class HistoryRunDto
{
    public int Id { get; set; }
    public string? InstanceId { get; set; }
    public int? JobId { get; set; }
    public string? JobSearchTerm { get; set; }
    public string? TriggerType { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? Status { get; set; }
    public int ListingsAdded { get; set; }
    public int ListingsSkipped { get; set; }
    public int ListingsFailed { get; set; }
    public int TotalListingsFound { get; set; }
    public int ListingsProcessed { get; set; }
    public string? CurrentPhase { get; set; }
    public string? ErrorMessage { get; set; }
    public int IssueCount { get; set; }
}
