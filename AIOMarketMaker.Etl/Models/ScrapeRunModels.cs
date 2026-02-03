namespace AIOMarketMaker.Etl.Models;

public record ManualScrapeRequest(int? JobId);

public record ErrorResponse(string Error);

public record EmptyJobsResponse(string Message, ScrapeRunResult[] Results);

public record ScrapeRunResult(int JobId, int RunId, string Status);

public record ManualScrapeResponse(string InstanceId, int RunId, IEnumerable<ScrapeRunResult> Results);

public record ScrapeJobConfig(int Id, string SearchTerm);

public record StartedScrapeRun(int RunId, int JobId, string Status, string InstanceId);
