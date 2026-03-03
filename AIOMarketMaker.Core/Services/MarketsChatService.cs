using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
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

public record ChatStreamEvent(string Type, object? Data);
public record ToolCallEvent(string Name, Dictionary<string, object?>? Arguments);
public record ToolResultEvent(string Name, string Summary);
public record ChatResponseEvent(string Message, ChatFilterState? Filters);
public record ChatErrorEvent(string Error);

public interface IMarketsChatService
{
    Task<MarketsChatResponse> Chat(int jobId, string searchTerm, MarketsChatRequest request);
    IAsyncEnumerable<ChatStreamEvent> ChatStream(int jobId, string searchTerm, MarketsChatRequest request, CancellationToken cancellationToken = default);
}

public class MarketsChatService : IMarketsChatService
{
    private record QueryToolSample(string? Title, string? Price, string? ListingStatus, string? Condition, int DaysOnMarket);

    private record QueryToolResult(
        int TotalCount, int ActiveCount, int SoldCount, int SellThrough,
        int AvgDaysToSell, decimal AvgPrice, decimal MinPrice, decimal MaxPrice,
        IEnumerable<QueryToolSample> SampleListings);

    private record SampleListingRow(
        string? Title, string? Price, string? ListingStatus, string? Condition,
        int DaysOnMarket, string? Url);

    private record SampleListingsResult(int TotalCount, int ReturnedCount, IEnumerable<SampleListingRow> Listings);

    private record ToolError(string Error);

    private record VariantCluster(
        int ClusterId,
        int Count,
        int Active,
        int Sold,
        int SellThroughPct,
        decimal MedianPrice,
        decimal Iqr,
        decimal MinPrice,
        decimal MaxPrice,
        IEnumerable<string> SampleTitles);

    private record ClusterListingProjection(int Id, string? Title, decimal? Price, string? ListingStatus);

    private record DiscoverVariantsResult(
        int TotalListings,
        int Sampled,
        int ClustersFound,
        double ElapsedSeconds,
        double Threshold,
        IEnumerable<VariantCluster> Clusters);

    private readonly IChatClient _chatClient;
    private readonly IMarketListingsQueryService _queryService;
    private readonly IClusteringService _clusteringService;
    private readonly EtlDbContext _db;
    private readonly ILogger<MarketsChatService> _logger;

    public MarketsChatService(
        IChatClient chatClient,
        IMarketListingsQueryService queryService,
        IClusteringService clusteringService,
        EtlDbContext db,
        ILogger<MarketsChatService> logger)
    {
        _chatClient = chatClient;
        _queryService = queryService;
        _clusteringService = clusteringService;
        _db = db;
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

        var sampleTool = BuildSampleListingsTool(jobId, () => toolCallCount++);

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
            Tools = [queryTool, setFiltersTool, sampleTool]
        };

        _logger.LogInformation("Chat started for job {JobId} with {HistoryCount} history messages. User: {Message}",
            jobId, request.History.Count(), request.Message);

        var sw = Stopwatch.StartNew();
        var response = await client.GetResponseAsync(messages, options);
        sw.Stop();

        _logger.LogInformation(
            "Chat complete in {ElapsedMs}ms. Tool calls: {ToolCallCount}, filters applied: {FiltersApplied}, response length: {ResponseLength}",
            sw.ElapsedMilliseconds, toolCallCount, capturedFilters != null, (response.Text ?? "").Length);

        if (capturedFilters != null)
        {
            _logger.LogInformation("Chat applied filters: {@Filters}", capturedFilters);
        }

        _logger.LogInformation("Chat response text: {ResponseText}", response.Text);

        return new MarketsChatResponse(response.Text ?? "", capturedFilters);
    }

    public async IAsyncEnumerable<ChatStreamEvent> ChatStream(
        int jobId,
        string searchTerm,
        MarketsChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatStreamEvent? finalEvent = null;

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

        var sampleTool = BuildSampleListingsTool(jobId, () => toolCallCount++);
        var discoverTool = BuildDiscoverVariantsTool(jobId, () => toolCallCount++);

        var toolMap = new Dictionary<string, AIFunction>
        {
            [queryTool.Name] = queryTool,
            [setFiltersTool.Name] = setFiltersTool,
            [sampleTool.Name] = sampleTool,
            [discoverTool.Name] = discoverTool
        };

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

        var options = new ChatOptions
        {
            Tools = new List<AITool> { queryTool, setFiltersTool, sampleTool, discoverTool }
        };

        var totalSw = Stopwatch.StartNew();
        _logger.LogInformation(
            "Stream started for job {JobId} ({SearchTerm}) with {HistoryCount} history messages. User: {Message}",
            jobId, searchTerm, request.History.Count(), request.Message);

        _logger.LogInformation("System prompt ({Length} chars): {SystemPrompt}", systemPrompt.Length, systemPrompt);

        if (request.CurrentFilters != null && HasActiveFilters(request.CurrentFilters))
        {
            _logger.LogInformation("Stream current filters: {@CurrentFilters}", request.CurrentFilters);
        }

        const int maxIterations = 10;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Stream iteration {Iteration}, message count: {MessageCount}", iteration, messages.Count);

            ChatResponse response;
            var llmSw = Stopwatch.StartNew();
            try
            {
                response = await _chatClient.GetResponseAsync(messages, options, cancellationToken);
                llmSw.Stop();
                _logger.LogInformation("LLM responded in {ElapsedMs}ms on iteration {Iteration}", llmSw.ElapsedMilliseconds, iteration);
            }
            catch (Exception ex)
            {
                llmSw.Stop();
                _logger.LogError(ex, "LLM call failed after {ElapsedMs}ms on iteration {Iteration}", llmSw.ElapsedMilliseconds, iteration);
                finalEvent = new ChatStreamEvent("error", new ChatErrorEvent("AI service temporarily unavailable. Try again."));
                break;
            }

            // Log token usage if available
            var usage = response.Usage;
            if (usage != null)
            {
                _logger.LogInformation(
                    "LLM usage on iteration {Iteration}: input={InputTokens}, output={OutputTokens}, total={TotalTokens}",
                    iteration, usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount);
            }

            var functionCalls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            // Log any reasoning text the LLM included alongside tool calls
            var reasoningText = response.Text;
            if (!string.IsNullOrWhiteSpace(reasoningText) && functionCalls.Count > 0)
            {
                _logger.LogInformation("LLM reasoning (iteration {Iteration}): {Reasoning}", iteration, reasoningText);
            }

            if (functionCalls.Count == 0)
            {
                var text = reasoningText ?? "";
                totalSw.Stop();

                _logger.LogInformation("Stream response text: {ResponseText}", text);

                if (capturedFilters != null)
                {
                    _logger.LogInformation("Stream applied filters: {@Filters}", capturedFilters);
                }

                _logger.LogInformation(
                    "Stream complete in {TotalMs}ms. Iterations: {Iterations}, tool calls: {ToolCallCount}, filters applied: {FiltersApplied}, response length: {ResponseLength}",
                    totalSw.ElapsedMilliseconds, iteration + 1, toolCallCount, capturedFilters != null, text.Length);

                finalEvent = new ChatStreamEvent("response", new ChatResponseEvent(text, capturedFilters));
                break;
            }

            // ChatResponse.Messages contains only the model's new messages (assistant + tool calls),
            // not the input messages we sent. Required by Microsoft.Extensions.AI contract.
            foreach (var assistantMessage in response.Messages)
            {
                messages.Add(assistantMessage);
            }

            foreach (var call in functionCalls)
            {
                var args = call.Arguments?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (object?)kvp.Value);

                // Unwrap JsonElement values so Serilog logs actual strings/numbers, not {"ValueKind":"String"}
                var loggableArgs = call.Arguments?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value is JsonElement je ? UnwrapJsonElement(je) : kvp.Value);

                _logger.LogInformation("Tool call: {ToolName} with args {@ToolArgs}", call.Name, loggableArgs);

                yield return new ChatStreamEvent("tool_call", new ToolCallEvent(call.Name, args));

                var toolSw = Stopwatch.StartNew();
                var summary = await InvokeTool(call, toolMap, messages, cancellationToken);
                toolSw.Stop();

                _logger.LogInformation("Tool result: {ToolName} in {ElapsedMs}ms — {Summary}", call.Name, toolSw.ElapsedMilliseconds, summary);

                yield return new ChatStreamEvent("tool_result", new ToolResultEvent(call.Name, summary));
            }
        }

        if (finalEvent == null)
        {
            totalSw.Stop();
            _logger.LogWarning("Stream hit max iterations ({Max}) for job {JobId} after {TotalMs}ms", maxIterations, jobId, totalSw.ElapsedMilliseconds);
            finalEvent = new ChatStreamEvent("response",
                new ChatResponseEvent("I've reached the maximum number of tool calls. Here's what I found so far.", capturedFilters));
        }

        yield return finalEvent;
    }

    private async Task<string> InvokeTool(
        FunctionCallContent call,
        Dictionary<string, AIFunction> toolMap,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!toolMap.TryGetValue(call.Name, out var tool))
            {
                var errorMsg = $"Error: Unknown tool '{call.Name}'";
                messages.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, errorMsg)]));
                return errorMsg;
            }

            var arguments = call.Arguments != null
                ? new AIFunctionArguments(call.Arguments)
                : new AIFunctionArguments();
            var result = await tool.InvokeAsync(arguments, cancellationToken);
            var resultString = result?.ToString() ?? "";

            _logger.LogInformation("Tool {ToolName} raw result ({Length} chars): {RawResult}",
                call.Name, resultString.Length, resultString);

            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, resultString)]));

            return BuildToolResultSummary(call.Name, resultString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} invocation failed", call.Name);
            var errorSummary = $"Error: {ex.Message}";
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, errorSummary)]));
            return errorSummary;
        }
    }

    private static string BuildToolResultSummary(string toolName, string resultJson)
    {
        try
        {
            if (toolName == "set_filters")
            {
                return "Filters applied";
            }

            if (toolName == "query_listings")
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("Error", out var errorProp))
                {
                    return $"Error: {errorProp.GetString()}";
                }

                var totalCount = root.GetProperty("TotalCount").GetInt32();
                var activeCount = root.GetProperty("ActiveCount").GetInt32();
                var soldCount = root.GetProperty("SoldCount").GetInt32();
                var avgPrice = root.GetProperty("AvgPrice").GetDecimal();
                return $"{totalCount} listings ({activeCount} active, {soldCount} sold) — avg \u00a3{avgPrice:F2}";
            }

            if (toolName == "sample_listings")
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("Error", out var errorProp))
                {
                    return $"Error: {errorProp.GetString()}";
                }

                var totalCount = root.GetProperty("TotalCount").GetInt32();
                var returnedCount = root.GetProperty("ReturnedCount").GetInt32();
                return $"Sampled {returnedCount} of {totalCount} listings";
            }

            if (toolName == "discover_variants")
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("Error", out var errorProp))
                {
                    return $"Error: {errorProp.GetString()}";
                }

                var clustersFound = root.GetProperty("ClustersFound").GetInt32();
                var sampled = root.GetProperty("Sampled").GetInt32();
                var elapsed = root.GetProperty("ElapsedSeconds").GetDouble();
                var usedThreshold = root.TryGetProperty("Threshold", out var threshProp) ? threshProp.GetDouble() : 1.5;
                return $"Found {clustersFound} variant clusters from {sampled} listings (threshold={usedThreshold:F1}, {elapsed:F1}s)";
            }

            return "Tool completed";
        }
        catch (Exception)
        {
            return "Tool completed";
        }
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

        var hasFilters = filters != null && HasActiveFilters(filters);
        if (hasFilters)
        {
            sb.AppendLine("Current UI filter state (set by the user or a previous chat turn):");
            if (!string.IsNullOrWhiteSpace(filters!.Regex))
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
        else
        {
            sb.AppendLine("No filters are currently active in the UI.");
            sb.AppendLine();
        }

        sb.AppendLine("""
            Important: The user can change filters directly in the UI between messages.
            The filter state above reflects what is currently applied. If it differs from
            what you set previously, the user changed it manually. Respect their changes
            and build on them rather than overwriting.

            All prices are in GBP (£). Always use £, never $.

            You have four tools:

            1. discover_variants — Clusters listing titles into product groups using TF-IDF
               text similarity + Ward hierarchical clustering. Always free and instant. The
               threshold parameter (default 1.5) controls granularity: lower values (0.5-1.0)
               produce many small tight clusters, higher values (2.0-3.0) produce fewer broader
               clusters. Every listing is assigned to a cluster (no noise). Returns clusters
               with stats (count, sell-through, median price, IQR, sample titles). Use this
               FIRST when the user asks about opportunities, variants, or what products exist.
               If clusters seem too broad, re-call with a lower threshold.

            2. query_listings — Test a filter combination. Returns aggregate stats (counts,
               prices, sell-through) and a small sample. Use this to verify a specific
               variant or test a regex pattern.

            3. set_filters — Push filters to the UI. The user sees listings update live.
               Always call this after query_listings so the user sees results.

            4. sample_listings — Browse raw listing rows. Returns individual titles, prices,
               statuses, conditions, days on market, and URLs. Use this to explore specific
               listings, spot patterns, or understand what's in a cluster.

            Finding opportunities (preferred workflow):
            1. Call discover_variants to find what product groups exist in the data
            2. Read the clusters — each one is a product variant with stats
            3. Identify the best clusters: high sell-through (>50%), tight IQR, decent volume
            4. For each promising cluster, read its sample titles and write a regex that
               captures that specific product
            5. Test the regex with query_listings to verify count and stats match
            6. Apply with set_filters so the user sees the variant
            7. Give a specific recommendation with data

            This is much more effective than manually guessing regex patterns. The clustering
            discovers the product groups statistically — you just need to translate the best
            ones into regex filters.

            Filtering workflow (when user asks for specific filters):
            1. Build a regex pattern for what the user describes
            2. Use query_listings to test it — pass all filter params explicitly
            3. IMMEDIATELY call set_filters to apply it so the user can see results
            4. Tell the user what you found and ask if they want to refine

            Exploration workflow:
            When the user asks about the data, use sample_listings to look at actual listings.
            Choose the right sampling strategy:
            - randomise=true for a representative overview of what's in the data
            - sortBy=price, sortDir=desc to find expensive outliers
            - sortBy=price, sortDir=asc to find cheapest/best value
            - sortBy=daysOnMarket, sortDir=asc, status=Sold to see what sells fastest
            - sortBy=daysOnMarket, sortDir=desc to find stale unsold listings

            Keep drilling until the price spread is tight. If a filtered set has a wide
            price range, it means you're looking at mixed products — sample the listings
            and split them further. A good leaf variant will have most sold prices within
            a 20-30% band of the median.

            Do NOT give generic category-level advice like "target the £30-120 range."
            The user needs a specific variant, a specific price point, and evidence from
            the data. If you can't find a clear opportunity, say so honestly.

            When comparing variants, always include BOTH active and sold listings (pass
            status=null). Filtering to Active-only hides sell-through data. Filtering to
            Sold-only hides current competition. You need both to assess an opportunity.

            CRITICAL RULES:
            - ALWAYS call set_filters after query_listings. Never query more than once
              without applying. The user needs to see results, not wait for perfection.
            - Do NOT loop query_listings multiple times trying to refine before applying.
              Apply first, refine later if the user asks.
            - One query + one set_filters per turn is the ideal pattern.
            - query_listings and sample_listings do NOT inherit UI filters — pass all
              filter params explicitly.
            - set_filters pushes filters to the UI. The user sees the listings update live.

            Regex refinement guardrails:
            - You have a MAXIMUM of 10 tool calls per turn. Budget them wisely.
            - If your regex isn't producing the right results after 2 attempts, STOP
              guessing and use sample_listings to inspect actual listing titles. Read
              what's there, then build the regex from evidence, not assumptions.
            - Never make more than 3 query_listings calls in a row without either
              applying filters or sampling listings. Blind regex tweaking wastes your
              budget and makes the user wait.
            - If the data simply doesn't support the filter the user wants (e.g. sellers
              don't consistently label "console only" in titles), say so honestly and
              suggest alternatives (e.g. price range filtering, manual review).
            - Always apply the best result you have before running out of budget. A
              partial filter the user can see is better than 10 failed attempts with
              no output.

            When refining, extend existing filters rather than replacing them.

            Regex tips:
            - ^(?!.*(exclusion1|exclusion2)) for negative lookaheads
            - .* between terms (title word order varies)
            - pok.mon to match accented characters
            - \d+\s*x or x\s*\d+ for multi-packs
            - Common exclusions: bundle, lot, case, set, job lot, wholesale

            Communication style:
            - Be brief but specific. Lead with the key finding and numbers.
            - Always cite the data: "67 sold, 45% sell-through, median £117"
            - Name the specific variant you're recommending, not the broad category.
            - Don't list every sample title — mention 1-2 if relevant.
            - Don't explain regex syntax unless asked.
            - Don't repeat information the user already knows.
            - Use plain language, not technical jargon.
            """);

        return sb.ToString();
    }

    private AIFunction BuildSampleListingsTool(int jobId, Action incrementToolCalls)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Regex pattern to filter listing titles. Pass null to use no regex filter.")] string? regex,
                [Description("Listing condition, e.g. 'New', 'Used'. Pass null for all.")] string? condition,
                [Description("Minimum price. Pass null for no minimum.")] decimal? minPrice,
                [Description("Maximum price. Pass null for no maximum.")] decimal? maxPrice,
                [Description("Minimum days on market. Pass null for no minimum.")] int? minDays,
                [Description("Maximum days on market. Pass null for no maximum.")] int? maxDays,
                [Description("Listing status: 'Active' or 'Sold'. Pass null for all.")] string? status,
                [Description("Column to sort by: 'title', 'price', 'listingStatus', 'condition', 'daysOnMarket'. Default: 'daysOnMarket'")] string? sortBy,
                [Description("Sort direction: 'asc' or 'desc'. Default: 'asc'")] string? sortDir,
                [Description("Number of listings to return (1-100). Default: 10")] int? limit,
                [Description("If true, return a random sample instead of sorted order")] bool? randomise
            ) =>
            {
                incrementToolCalls();
                try
                {
                    var effectiveLimit = Math.Clamp(limit ?? 10, 1, 100);
                    var fetchSize = (randomise == true) ? 200 : effectiveLimit;

                    var result = await _queryService.Query(new ListingsQueryParams(
                        jobId, status, null, condition, minPrice, maxPrice,
                        minDays, maxDays, regex, sortBy ?? "daysOnMarket", sortDir ?? "asc", 1, fetchSize));

                    var items = result.Items;
                    if (randomise == true)
                    {
                        var rng = new Random();
                        items = items.OrderBy(_ => rng.Next()).Take(effectiveLimit);
                    }

                    var rows = items.Select(l => new SampleListingRow(
                        l.Title, l.Price?.ToString("F2"), l.ListingStatus, l.Condition, l.DaysOnMarket, l.Url));

                    return JsonSerializer.Serialize(new SampleListingsResult(result.TotalCount, effectiveLimit, rows));
                }
                catch (RegexParseException)
                {
                    return JsonSerializer.Serialize(new ToolError("Invalid regex pattern. Fix the syntax and try again."));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "sample_listings tool failed");
                    return JsonSerializer.Serialize(new ToolError("Query failed. Try a different filter."));
                }
            },
            "sample_listings",
            "Browse raw listing data from the current view. Returns individual listing rows with title, price, status, condition, days on market, and URL. Use this to explore the data, spot patterns, check outliers, or understand what's in the dataset before filtering. Supports sorting, limiting, and random sampling.");
    }

    private static bool HasActiveFilters(ChatFilterState f) =>
        !string.IsNullOrWhiteSpace(f.Regex) ||
        !string.IsNullOrWhiteSpace(f.Condition) ||
        !string.IsNullOrWhiteSpace(f.Status) ||
        f.MinPrice.HasValue || f.MaxPrice.HasValue ||
        f.MinDays.HasValue || f.MaxDays.HasValue;

    private static object? UnwrapJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je.GetRawText()
    };

    private AIFunction BuildDiscoverVariantsTool(int jobId, Action incrementToolCalls)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Maximum number of listings to sample for clustering. Default 2000, max 3000.")] int? maxListings,
                [Description("Ward clustering threshold. Lower = more smaller clusters. Higher = fewer larger clusters. Default 1.5, range 0.5-3.0.")] double? threshold
            ) =>
            {
                incrementToolCalls();
                var sw = Stopwatch.StartNew();
                var effectiveThreshold = Math.Clamp(threshold ?? 1.5, 0.5, 3.0);

                try
                {
                    var sampleSize = Math.Min(maxListings ?? 2000, 3000);

                    var listings = await _db.Listings
                        .Where(l => l.ScrapeJobId == jobId && l.Title != null && l.Price != null)
                        .Select(l => new ClusterListingProjection(l.Id, l.Title, l.Price, l.ListingStatus))
                        .ToListAsync();

                    _logger.LogInformation("discover_variants: loaded {Count} listings for job {JobId}, threshold={Threshold}",
                        listings.Count, jobId, effectiveThreshold);

                    if (listings.Count == 0)
                    {
                        return JsonSerializer.Serialize(new ToolError("No listings found for this job."));
                    }

                    var totalCount = listings.Count;
                    if (listings.Count > sampleSize)
                    {
                        var rng = new Random(42);
                        listings = listings.OrderBy(_ => rng.Next()).Take(sampleSize).ToList();
                    }

                    var titles = listings.Select(l => l.Title!).ToList();
                    var ids = listings.Select(l => l.Id).ToList();

                    _logger.LogInformation("discover_variants: TF-IDF + Ward clustering {Count} titles with threshold={Threshold}",
                        titles.Count, effectiveThreshold);
                    var clusterResult = _clusteringService.ClusterByText(ids, titles, effectiveThreshold);

                    var clusters = BuildVariantClusters(clusterResult, listings);

                    sw.Stop();
                    _logger.LogInformation(
                        "discover_variants: found {ClusterCount} clusters from {Count} listings in {Elapsed:F1}s (threshold={Threshold})",
                        clusters.Count, listings.Count, sw.Elapsed.TotalSeconds, effectiveThreshold);

                    var result = new DiscoverVariantsResult(
                        totalCount,
                        listings.Count,
                        clusters.Count,
                        Math.Round(sw.Elapsed.TotalSeconds, 1),
                        effectiveThreshold,
                        clusters);

                    return JsonSerializer.Serialize(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "discover_variants tool failed");
                    return JsonSerializer.Serialize(new ToolError($"Clustering failed: {ex.Message}"));
                }
            },
            "discover_variants",
            "Discover product variants using TF-IDF + Ward hierarchical clustering. The threshold parameter controls cluster granularity: lower values (0.5-1.0) produce many small tight clusters, higher values (1.5-3.0) produce fewer broader clusters. Returns clusters sorted by size with stats.");
    }

    private static List<VariantCluster> BuildVariantClusters(
        ClusteringResult clusterResult,
        List<ClusterListingProjection> listings)
    {
        var idToListing = listings.ToDictionary(l => l.Id);
        var clusters = new List<VariantCluster>();

        foreach (var cluster in clusterResult.Clusters.OrderByDescending(c => c.Items.Count))
        {
            if (cluster.Items.Count < 5)
            {
                continue;
            }

            var clusterListings = cluster.Items
                .Where(e => idToListing.ContainsKey(e.Id))
                .Select(e => idToListing[e.Id])
                .ToList();

            var prices = clusterListings
                .Where(l => l.Price.HasValue)
                .Select(l => l.Price!.Value)
                .OrderBy(p => p)
                .ToList();

            if (prices.Count == 0)
            {
                continue;
            }

            var active = clusterListings.Count(l => l.ListingStatus == "Active");
            var sold = clusterListings.Count(l => l.ListingStatus == "Sold");
            var total = active + sold;
            var st = total > 0 ? (int)Math.Round((double)sold / total * 100) : 0;
            var median = prices[prices.Count / 2];
            var q1 = prices[prices.Count / 4];
            var q3 = prices[prices.Count * 3 / 4];
            var iqr = q3 - q1;

            // Sample titles: mix of sold and active
            var soldTitles = clusterListings
                .Where(l => l.ListingStatus == "Sold")
                .Take(3)
                .Select(l => l.Title!);
            var activeTitles = clusterListings
                .Where(l => l.ListingStatus == "Active")
                .Take(2)
                .Select(l => l.Title!);
            var sampleTitles = soldTitles.Concat(activeTitles).Take(5);

            clusters.Add(new VariantCluster(
                cluster.Label,
                clusterListings.Count,
                active, sold, st,
                median, iqr,
                prices.First(), prices.Last(),
                sampleTitles));
        }

        return clusters;
    }
}
