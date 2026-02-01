namespace AIOMarketMaker.Etl.Models;

/// <summary>
/// Message format for the scrape-jobs queue.
/// Contains all information needed to run a scrape for a job.
/// </summary>
public record ScrapeJobMessage(
    int ScrapeRunId,
    int JobId,
    string SearchTerm,
    string TriggerType
);
