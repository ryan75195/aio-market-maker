using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record MarketsChatRequest(
    string Message,
    IEnumerable<ChatHistoryEntry> History,
    ChatFilterState? CurrentFilters);

public record ChatHistoryEntry(string Role, string Content);

public record ChatFilterState(
    string? Regex,
    string? Condition,
    decimal? MinPrice,
    decimal? MaxPrice,
    int? MinDays,
    int? MaxDays,
    string? Status);

public record MarketsChatResponse(string Message, ChatFilterState? Filters);

public interface IMarketsChatService
{
    Task<MarketsChatResponse> Chat(int jobId, string searchTerm, MarketsChatRequest request);
}

public class MarketsChatService : IMarketsChatService
{
    private record QueryToolSample(string? Title, string? Price, string? ListingStatus, string? Condition, int DaysOnMarket);

    private record QueryToolResult(
        int TotalCount, int ActiveCount, int SoldCount, int SellThrough,
        int AvgDaysToSell, decimal AvgPrice, decimal MinPrice, decimal MaxPrice,
        IEnumerable<QueryToolSample> SampleListings);

    private record ToolError(string Error);

    private readonly IChatClient _chatClient;
    private readonly IMarketListingsQueryService _queryService;
    private readonly ILogger<MarketsChatService> _logger;

    public MarketsChatService(
        IChatClient chatClient,
        IMarketListingsQueryService queryService,
        ILogger<MarketsChatService> logger)
    {
        _chatClient = chatClient;
        _queryService = queryService;
        _logger = logger;
    }

    public async Task<MarketsChatResponse> Chat(int jobId, string searchTerm, MarketsChatRequest request)
    {
        ChatFilterState? capturedFilters = null;
        var toolCallCount = 0;

        var queryTool = AIFunctionFactory.Create(
            async (
                [Description("Regex pattern to filter listing titles")] string? regex,
                [Description("Listing condition, e.g. 'New', 'Used'")] string? condition,
                [Description("Minimum price")] decimal? minPrice,
                [Description("Maximum price")] decimal? maxPrice,
                [Description("Minimum days on market")] int? minDays,
                [Description("Maximum days on market")] int? maxDays,
                [Description("Listing status: 'Active' or 'Sold'")] string? status
            ) =>
            {
                toolCallCount++;
                try
                {
                    var result = await _queryService.Query(new ListingsQueryParams(
                        jobId, status, null, condition, minPrice, maxPrice,
                        minDays, maxDays, regex, "daysOnMarket", "asc", 1, 20));

                    var sample = result.Items.Select(l => new QueryToolSample(
                        l.Title, l.Price?.ToString("F2"), l.ListingStatus, l.Condition, l.DaysOnMarket));

                    return JsonSerializer.Serialize(new QueryToolResult(
                        result.TotalCount,
                        result.Stats.ActiveCount,
                        result.Stats.SoldCount,
                        result.Stats.SellThrough,
                        result.Stats.AvgDaysToSell,
                        result.Stats.AvgPrice,
                        result.Stats.MinPrice,
                        result.Stats.MaxPrice,
                        sample));
                }
                catch (RegexParseException)
                {
                    return JsonSerializer.Serialize(new ToolError("Invalid regex pattern. Fix the syntax and try again."));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "query_listings tool failed");
                    return JsonSerializer.Serialize(new ToolError("Query failed. Try a different filter."));
                }
            },
            "query_listings",
            "Query listings in the current job with filters. Returns count, price stats, and sample titles. Use this to test regex patterns before applying them.");

        var setFiltersTool = AIFunctionFactory.Create(
            (
                [Description("Regex pattern to filter listing titles")] string? regex,
                [Description("Listing condition, e.g. 'New', 'Used'")] string? condition,
                [Description("Minimum price")] decimal? minPrice,
                [Description("Maximum price")] decimal? maxPrice,
                [Description("Minimum days on market")] int? minDays,
                [Description("Maximum days on market")] int? maxDays,
                [Description("Listing status: 'Active' or 'Sold'")] string? status
            ) =>
            {
                toolCallCount++;
                capturedFilters = new ChatFilterState(regex, condition, minPrice, maxPrice, minDays, maxDays, status);
                return "Filters applied successfully. The UI will update to show these results.";
            },
            "set_filters",
            "Apply filters to the listings view. Call this when you have a good filter combination to show the user. The UI updates automatically.");

        var systemPrompt = BuildSystemPrompt(searchTerm, request.CurrentFilters);

        var messages = new List<ChatMessage> { new(ChatRole.System, systemPrompt) };
        foreach (var h in request.History)
        {
            var role = h.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User
                : ChatRole.Assistant;
            messages.Add(new ChatMessage(role, h.Content));
        }
        messages.Add(new ChatMessage(ChatRole.User, request.Message));

        var client = new ChatClientBuilder(_chatClient)
            .UseFunctionInvocation()
            .Build();

        var options = new ChatOptions
        {
            Tools = [queryTool, setFiltersTool]
        };

        _logger.LogInformation("Starting chat for job {JobId}, message: {Message}", jobId, request.Message);

        var response = await client.GetResponseAsync(messages, options);

        _logger.LogInformation("Chat complete. Tool calls: {Count}, filters applied: {Applied}",
            toolCallCount, capturedFilters != null);

        return new MarketsChatResponse(response.Text ?? "", capturedFilters);
    }

    private static string BuildSystemPrompt(string searchTerm, ChatFilterState? filters)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"""
            You are a market analyst assistant for eBay arbitrage. You help users isolate
            specific product variants within a broad search category by building regex
            filters and analyzing the resulting listings.

            You are currently viewing job "{searchTerm}".

            """);

        if (filters != null && HasActiveFilters(filters))
        {
            sb.AppendLine("Current active filters:");
            if (!string.IsNullOrWhiteSpace(filters.Regex))
            {
                sb.AppendLine($"- Regex: {filters.Regex}");
            }
            if (!string.IsNullOrWhiteSpace(filters.Status))
            {
                sb.AppendLine($"- Status: {filters.Status}");
            }
            if (!string.IsNullOrWhiteSpace(filters.Condition))
            {
                sb.AppendLine($"- Condition: {filters.Condition}");
            }
            if (filters.MinPrice.HasValue)
            {
                sb.AppendLine($"- Min Price: £{filters.MinPrice.Value}");
            }
            if (filters.MaxPrice.HasValue)
            {
                sb.AppendLine($"- Max Price: £{filters.MaxPrice.Value}");
            }
            if (filters.MinDays.HasValue)
            {
                sb.AppendLine($"- Min Days: {filters.MinDays.Value}");
            }
            if (filters.MaxDays.HasValue)
            {
                sb.AppendLine($"- Max Days: {filters.MaxDays.Value}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("""
            Your workflow:
            1. When the user describes a variant, build a regex pattern to match it
            2. Use query_listings to test the pattern and review the results
            3. Check sample titles for contamination (bundles, wrong variants, accessories)
            4. Refine the regex with negative lookaheads to exclude bad matches
            5. Once the cluster looks clean, call set_filters to apply it
            6. Report what you found: count, price stats, cluster quality

            When the user asks to refine, modify the existing filters rather than starting
            from scratch. For regex, extend the existing pattern (e.g. add terms to an
            existing negative lookahead) rather than replacing it.

            Regex tips for eBay titles:
            - Use ^(?!.*(exclusion1|exclusion2)) for negative lookaheads at the start
            - Use .* between terms since title word order varies
            - Use pok.mon to match both Pokemon and Pokémon (accented e)
            - Use \d+\s*x or x\s*\d+ to catch multi-packs like "x10" or "3x"
            - Common exclusions: bundle, lot, case, set, job lot, wholesale

            Be concise. Report numbers. Don't explain regex syntax unless asked.
            """);

        return sb.ToString();
    }

    private static bool HasActiveFilters(ChatFilterState f) =>
        !string.IsNullOrWhiteSpace(f.Regex) ||
        !string.IsNullOrWhiteSpace(f.Condition) ||
        !string.IsNullOrWhiteSpace(f.Status) ||
        f.MinPrice.HasValue || f.MaxPrice.HasValue ||
        f.MinDays.HasValue || f.MaxDays.HasValue;
}
