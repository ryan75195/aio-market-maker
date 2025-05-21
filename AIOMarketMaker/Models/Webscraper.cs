// FILE: Domain.cs
using System.Text.Json.Serialization;

namespace ScraperWorker.Services;

public enum ContainerState
{
    Complete,
    Created,
    Running,
    Exited,
    Paused,
    Unknown
}
public record ProxyConfig(
        string Host,
        int Port,
        string? Username,
        string? Password,
        string Type = "SOCKS5"
    );

public record StartResponse(
    string JobId
);

public record ResultsResponse(
     string JobId,
     string Logs
 );

public record ScrapeProgressResponse(
    long ProcessedCount,
    long SuccessCount,
    long FailureCount,
    DateTimeOffset Timestamp,
    string CurrentUrl
);

public record StatusResponse(
    string JobId,
    ContainerState State
);

public record StartRequest(
       IEnumerable<string> Urls,
       IEnumerable<ProxyConfig>? Proxies = null
   );

public record UrlRequest(
    string Id,
    string Url
);

//public record class JobEntity
//{
//    public DateTime StartedAt { get; init; }
//    public DateTime? CompletedAt { get; init; }
//    public JobStatusType Status { get; init; }

//    public long TotalItems { get; init; }
//    public long Processed { get; init; }
//    public long Success { get; init; }
//    public long Failure { get; init; }
//    public string? CurrentUrl { get; init; }

//    [JsonIgnore]
//    public TimeSpan? Eta
//    {
//        get
//        {
//            if (Processed <= 0 || TotalItems <= Processed)
//                return null;
//            var end = CompletedAt ?? DateTime.UtcNow;
//            var elapsed = end - StartedAt;
//            var secsPer = elapsed.TotalSeconds / Processed;
//            var remain = TotalItems - Processed;
//            return TimeSpan.FromSeconds(secsPer * remain);
//        }
//    }

//    public JobEntity(
//        string jobId,
//        DateTime startedAt,
//        JobStatusType status,
//        long totalItems,
//        long processed = 0,
//        long success = 0,
//        long failure = 0,
//        string? currentUrl = null,
//        DateTime? completedAt = null
//    )
//    {
//        StartedAt = startedAt;
//        Status = status;
//        TotalItems = totalItems;
//        Processed = processed;
//        Success = success;
//        Failure = failure;
//        CurrentUrl = currentUrl;
//        CompletedAt = completedAt;
//    }

//    public JobEntity() { }

//    public string ToLogString()
//    {
//        // percentage complete (guard divide-by-zero)
//        var pct = TotalItems > 0
//            ? (Processed * 100d / TotalItems)
//            : 0;

//        // how long it’s been running (or total duration if finished)
//        var elapsed = (CompletedAt ?? DateTime.UtcNow) - StartedAt;

//        // ETA or “N/A”
//        var etaText = Eta.HasValue
//            ? Eta.Value.ToString(@"hh\:mm\:ss")
//            : "N/A";

//        return string.Concat(
//            $"[Job] Status={Status} | ",
//            $"Progress={Processed}/{TotalItems} ({pct:F1}%) | ",
//            $"Success={Success}, Failure={Failure} | ",
//            $"Elapsed={elapsed:hh\\:mm\\:ss} | ",
//            $"ETA={etaText} | ",
//            $"StartedAt={StartedAt:O} | ",
//            $"CurrentUrl={(CurrentUrl ?? "–")}"
//        );
//    }
//}

public static class TableKeys
{
    // Simple deterministic row-key helper
    public static string FromUrl(string url) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url))
               .TrimEnd('='); // Azure Table friendly
}
