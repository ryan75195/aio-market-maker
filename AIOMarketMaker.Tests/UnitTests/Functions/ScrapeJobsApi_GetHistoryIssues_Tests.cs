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
    public async Task Should_return_issues_from_ScrapeRunIssues()
    {
        // Arrange
        var run = await SeedScrapeRun();
        _dbContext.ScrapeRunIssues.Add(new ScrapeRunIssue
        {
            ScrapeRunId = run.Id,
            ListingId = "123456789",
            IssueType = "DescriptionFetchFailed",
            ErrorMessage = "Connection timeout",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-3)
        });
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
            Assert.That(issues[0].Status, Is.EqualTo("Failed"));
            Assert.That(issues[0].IssueType, Is.EqualTo("DescriptionFetchFailed"));
            Assert.That(issues[0].ErrorMessage, Is.EqualTo("Connection timeout"));
        });
    }

    [Test]
    public async Task Should_return_multiple_issues_ordered_by_date()
    {
        // Arrange
        var run = await SeedScrapeRun();

        _dbContext.ScrapeRunIssues.AddRange(
            new ScrapeRunIssue
            {
                ScrapeRunId = run.Id,
                ListingId = "111111111",
                IssueType = "DescriptionFetchFailed",
                ErrorMessage = "Timeout",
                CreatedUtc = DateTime.UtcNow.AddMinutes(-5)
            },
            new ScrapeRunIssue
            {
                ScrapeRunId = run.Id,
                ListingId = "222222222",
                IssueType = "ParseFailure",
                ErrorMessage = "Parse retries exhausted",
                CreatedUtc = DateTime.UtcNow.AddMinutes(-2)
            }
        );
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
        Assert.Multiple(() =>
        {
            Assert.That(issues![0].ListingId, Is.EqualTo("111111111"));
            Assert.That(issues[1].ListingId, Is.EqualTo("222222222"));
        });
    }

    [Test]
    public async Task Should_return_empty_list_when_no_issues()
    {
        // Arrange
        var run = await SeedScrapeRun();

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
