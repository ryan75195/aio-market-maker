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
    public async Task Should_include_issues_in_IssueCount()
    {
        // Arrange - Create a run with 3 issues
        var run = await SeedScrapeRun();

        _dbContext.ScrapeRunIssues.AddRange(
            new ScrapeRunIssue
            {
                ScrapeRunId = run.Id,
                ListingId = "111111111",
                IssueType = "DescriptionFetchFailed",
                ErrorMessage = "Timeout",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run.Id,
                ListingId = "222222222",
                IssueType = "DescriptionFetchFailed",
                ErrorMessage = "Connection refused",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run.Id,
                ListingId = "333333333",
                IssueType = "ParseFailure",
                ErrorMessage = "Parse retries exhausted",
                CreatedUtc = DateTime.UtcNow
            }
        );
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
        Assert.That(resultRun!.IssueCount, Is.EqualTo(3), "IssueCount should count all ScrapeRunIssues");
    }

    [Test]
    public async Task Should_return_zero_IssueCount_when_no_issues()
    {
        // Arrange - Create a run with no issues
        var run = await SeedScrapeRun();

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
    public async Task Should_correctly_calculate_IssueCount_for_multiple_runs()
    {
        // Arrange - Create two runs with different issue counts
        var run1 = await SeedScrapeRun();

        // Run 1: 2 issues
        _dbContext.ScrapeRunIssues.AddRange(
            new ScrapeRunIssue
            {
                ScrapeRunId = run1.Id,
                ListingId = "aaa111",
                IssueType = "DescriptionFetchFailed",
                ErrorMessage = "Timeout",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run1.Id,
                ListingId = "aaa222",
                IssueType = "ParseFailure",
                ErrorMessage = "Parse retries exhausted",
                CreatedUtc = DateTime.UtcNow
            }
        );
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

        // Run 2: 3 issues
        _dbContext.ScrapeRunIssues.AddRange(
            new ScrapeRunIssue
            {
                ScrapeRunId = run2.Id,
                ListingId = "bbb111",
                IssueType = "DescriptionFetchFailed",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run2.Id,
                ListingId = "bbb222",
                IssueType = "DescriptionFetchFailed",
                CreatedUtc = DateTime.UtcNow
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run2.Id,
                ListingId = "bbb333",
                IssueType = "ParseFailure",
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
            Assert.That(resultRun1!.IssueCount, Is.EqualTo(2), "Run 1 should have 2 issues");
            Assert.That(resultRun2!.IssueCount, Is.EqualTo(3), "Run 2 should have 3 issues");
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
