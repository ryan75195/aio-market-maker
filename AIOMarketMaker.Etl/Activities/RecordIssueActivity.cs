using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Activities;

public record RecordIssueRequest(int ScrapeRunId, string ListingId, string IssueType, string ErrorMessage);

public class RecordIssueActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<RecordIssueActivity> _logger;

    public RecordIssueActivity(EtlDbContext dbContext, ILogger<RecordIssueActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(RecordIssueActivity))]
    public async Task Run([ActivityTrigger] RecordIssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ListingId) || string.IsNullOrWhiteSpace(request.IssueType))
        {
            _logger.LogWarning("Received invalid RecordIssueRequest with empty ListingId or IssueType");
            return;
        }

        _logger.LogInformation("Recording issue for listing {ListingId}: {IssueType}", request.ListingId, request.IssueType);

        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = request.ScrapeRunId,
            ListingId = request.ListingId,
            IssueType = request.IssueType,
            ErrorMessage = request.ErrorMessage,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Recorded issue {IssueId} for ScrapeRun {ScrapeRunId}", issue.Id, request.ScrapeRunId);
    }
}
