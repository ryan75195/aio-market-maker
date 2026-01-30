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
public class ScrapeJobsApi_GetHistoryIssues_Tests
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
    public async Task Should_return_404_when_run_not_found()
    {
        // Arrange
        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, 999);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Should_return_retrying_listings_when_ParseAttempts_greater_than_zero_and_status_not_terminal()
    {
        // Arrange
        var run = await SeedScrapeRun();
        var retryingListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "123456789",
            Status = "Pending",
            ParseAttempts = 2,
            FailureDetails = "Missing: title, price",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-3)
        };
        _dbContext.ScrapeRunListings.Add(retryingListing);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(issues![0].ListingId, Is.EqualTo("123456789"));
            Assert.That(issues[0].Status, Is.EqualTo("Retrying"));
            Assert.That(issues[0].ParseAttempts, Is.EqualTo(2));
            Assert.That(issues[0].IssueType, Is.Null);
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Missing: title, price"));
        });
    }

    [Test]
    public async Task Should_return_failed_listings_from_ScrapeRunIssues()
    {
        // Arrange
        var run = await SeedScrapeRun();
        var failedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "987654321",
            Status = "Failed",
            ParseAttempts = 3,
            FailureDetails = "Missing: title, price, images",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
        };
        _dbContext.ScrapeRunListings.Add(failedListing);

        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = run.Id,
            ListingId = "987654321",
            IssueType = "ParseFailure",
            ErrorMessage = "Parse retries exhausted",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(issues![0].ListingId, Is.EqualTo("987654321"));
            Assert.That(issues[0].Status, Is.EqualTo("Failed"));
            Assert.That(issues[0].ParseAttempts, Is.EqualTo(3));
            Assert.That(issues[0].IssueType, Is.EqualTo("ParseFailure"));
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Parse retries exhausted"));
        });
    }

    [Test]
    public async Task Should_return_both_retrying_and_failed_listings_combined()
    {
        // Arrange
        var run = await SeedScrapeRun();

        // Retrying listing (ParseAttempts > 0, not terminal status)
        var retryingListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "111111111",
            Status = "Pending",
            ParseAttempts = 1,
            FailureDetails = "Missing: price",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        _dbContext.ScrapeRunListings.Add(retryingListing);

        // Failed listing with issue record
        var failedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "222222222",
            Status = "Failed",
            ParseAttempts = 3,
            FailureDetails = "Missing: title, price",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-3)
        };
        _dbContext.ScrapeRunListings.Add(failedListing);

        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = run.Id,
            ListingId = "222222222",
            IssueType = "ParseFailure",
            ErrorMessage = "Parse retries exhausted",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
        };
        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Has.Count.EqualTo(2));

        var retrying = issues!.FirstOrDefault(i => i.ListingId == "111111111");
        var failed = issues.FirstOrDefault(i => i.ListingId == "222222222");

        Assert.Multiple(() =>
        {
            Assert.That(retrying, Is.Not.Null, "Should include retrying listing");
            Assert.That(retrying!.Status, Is.EqualTo("Retrying"));
            Assert.That(retrying.IssueType, Is.Null);

            Assert.That(failed, Is.Not.Null, "Should include failed listing");
            Assert.That(failed!.Status, Is.EqualTo("Failed"));
            Assert.That(failed.IssueType, Is.EqualTo("ParseFailure"));
        });
    }

    [Test]
    public async Task Should_not_include_listings_with_zero_ParseAttempts()
    {
        // Arrange
        var run = await SeedScrapeRun();
        var normalListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "333333333",
            Status = "Pending",
            ParseAttempts = 0,  // No retries yet, should NOT appear
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(normalListing);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Is.Empty, "Listings with ParseAttempts=0 should not be included");
    }

    [Test]
    public async Task Should_not_include_completed_listings_in_retrying()
    {
        // Arrange
        var run = await SeedScrapeRun();
        var completedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "444444444",
            Status = "Complete",
            ParseAttempts = 2,  // Had retries but completed successfully
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(completedListing);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Is.Empty, "Completed listings should not appear as retrying");
    }

    [Test]
    public async Task Should_not_include_failed_listings_in_retrying_section()
    {
        // Arrange - Failed listings should only appear if they have a ScrapeRunIssue record
        var run = await SeedScrapeRun();
        var failedListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "555555555",
            Status = "Failed",
            ParseAttempts = 3,
            FailureDetails = "Missing: all fields",
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(failedListing);
        // No ScrapeRunIssue record - this shouldn't appear as retrying
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // The failed listing without a ScrapeRunIssue is expected since failures are tracked via issues table
        // But it should not appear as "Retrying"
        Assert.That(issues!.Any(i => i.Status == "Retrying"), Is.False,
            "Failed listings should not appear with Retrying status");
    }

    [Test]
    public async Task Should_return_empty_list_when_no_issues_or_retries()
    {
        // Arrange
        var run = await SeedScrapeRun();
        var normalListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "666666666",
            Status = "Complete",
            ParseAttempts = 0,
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(normalListing);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues, Is.Empty);
    }

    [Test]
    public async Task Should_use_FailureDetails_as_ErrorMessage_for_retrying_listings()
    {
        // Arrange - Retrying listings use FailureDetails for the error message
        var run = await SeedScrapeRun();
        var retryingListing = new ScrapeRunListing
        {
            ScrapeRunId = run.Id,
            ScrapeJobId = run.JobId!.Value,
            ListingId = "777777777",
            Status = "Pending",
            ParseAttempts = 1,
            FailureDetails = "Missing: description, images",
            ErrorMessage = null,  // ErrorMessage might be null
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.ScrapeRunListings.Add(retryingListing);
        await _dbContext.SaveChangesAsync();

        var request = CreateMockRequest();

        // Act
        var response = await _api.GetHistoryIssues(request, run.Id);

        // Assert
        response.Body.Position = 0;
        var json = await new StreamReader(response.Body).ReadToEndAsync();
        var issues = JsonSerializer.Deserialize<List<HistoryIssueDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.That(issues![0].ErrorMessage, Is.EqualTo("Missing: description, images"));
    }
}

/// <summary>
/// DTO for deserializing the GetHistoryIssues response in tests
/// </summary>
public class HistoryIssueDto
{
    public string? ListingId { get; set; }
    public string? Status { get; set; }
    public int ParseAttempts { get; set; }
    public string? IssueType { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; }
}
