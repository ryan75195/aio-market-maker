using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

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
    private record QueryToolSample(string? Title, string? Price, string? ListingStatus, string? Condition, int DaysOnMarket, string? Url);

    private record QueryToolResult(
        int TotalCount, int ActiveCount, int SoldCount, int SellThrough,
        int AvgDaysToSell, decimal MedianPrice, decimal Iqr,
        decimal MedianActivePrice, decimal MedianSoldPrice,
        decimal MinPrice, decimal MaxPrice,
        IEnumerable<QueryToolSample> SampleListings);

    private record VariantCluster(
        int ClusterId,
        int Count,
        int Active,
        int Sold,
        int SellThroughPct,
        decimal MedianPrice,
        decimal Iqr,
        decimal MedianActivePrice,
        decimal MedianSoldPrice,
        decimal MinPrice,
        decimal MaxPrice,
        string? Condition,
        string? Label,
        string? SuggestedRegex,
        [property: JsonIgnore] IEnumerable<string> SampleTitles);

    private record ClusterAnnotation(int ClusterId, string Label, string Regex);


    private record ClusterListingProjection(int Id, string? Title, decimal? Price, string? ListingStatus, string? Condition);

    private record DiscoverVariantsResult(
        int TotalListings,
        int Sampled,
        int ClustersFound,
        double ElapsedSeconds,
        double Threshold,
        IEnumerable<VariantCluster> Clusters);

    private readonly IChatClient _chatClient;
    private readonly ChatClient _annotationClient;
    private readonly IMarketListingsQueryService _queryService;
    private readonly IClusteringService _clusteringService;
    private readonly EtlDbContext _db;
    private readonly ILogger<MarketsChatService> _logger;

    public MarketsChatService(
        IChatClient chatClient,
        ChatClient annotationClient,
        IMarketListingsQueryService queryService,
        IClusteringService clusteringService,
        EtlDbContext db,
        ILogger<MarketsChatService> logger)
    {
        _chatClient = chatClient;
        _annotationClient = annotationClient;
        _queryService = queryService;
        _clusteringService = clusteringService;
        _db = db;
        _logger = logger;
    }

    public async Task<MarketsChatResponse> Chat(int jobId, string searchTerm, MarketsChatRequest request)
    {
        ChatFilterState? capturedFilters = null;
        var toolCallCount = 0;

        var queryTool = BuildQueryListingsTool(jobId, () => toolCallCount++, f => capturedFilters = f);
        var discoverTool = BuildDiscoverVariantsTool(jobId, searchTerm, () => toolCallCount++);

        var systemPrompt = BuildSystemPrompt(searchTerm, request.CurrentFilters);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage> { new(ChatRole.System, systemPrompt) };
        foreach (var h in request.History)
        {
            var role = h.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User
                : ChatRole.Assistant;
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(role, h.Content));
        }
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, request.Message));

        var client = new ChatClientBuilder(_chatClient)
            .UseFunctionInvocation()
            .Build();

        var options = new ChatOptions
        {
            Tools = [queryTool, discoverTool]
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

        var queryTool = BuildQueryListingsTool(jobId, () => toolCallCount++, f => capturedFilters = f);
        var discoverTool = BuildDiscoverVariantsTool(jobId, searchTerm, () => toolCallCount++);

        var toolMap = new Dictionary<string, AIFunction>
        {
            [queryTool.Name] = queryTool,
            [discoverTool.Name] = discoverTool
        };

        var systemPrompt = BuildSystemPrompt(searchTerm, request.CurrentFilters);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage> { new(ChatRole.System, systemPrompt) };
        foreach (var h in request.History)
        {
            var role = h.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.User
                : ChatRole.Assistant;
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(role, h.Content));
        }
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, request.Message));

        var options = new ChatOptions
        {
            Tools = new List<AITool> { queryTool, discoverTool }
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
        List<Microsoft.Extensions.AI.ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!toolMap.TryGetValue(call.Name, out var tool))
            {
                var errorMsg = $"Error: Unknown tool '{call.Name}'";
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(call.CallId, errorMsg)]));
                return errorMsg;
            }

            var rawArgs = call.Arguments?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                ?? new Dictionary<string, object?>();
            FillOptionalDefaults(call.Name, rawArgs);
            var arguments = new AIFunctionArguments(rawArgs);
            var result = await tool.InvokeAsync(arguments, cancellationToken);
            var resultString = result?.ToString() ?? "";

            _logger.LogInformation("Tool {ToolName} raw result ({Length} chars): {RawResult}",
                call.Name, resultString.Length, resultString);

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, resultString)]));

            return BuildToolResultSummary(call.Name, resultString);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} invocation failed", call.Name);
            var errorSummary = $"Error: {ex.Message}";
            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, errorSummary)]));
            return errorSummary;
        }
    }

    private static string BuildToolResultSummary(string toolName, string resultJson)
    {
        try
        {
            if (toolName == "query_listings")
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("Error", out var errorProp))
                {
                    return $"Error: {errorProp.GetString()}";
                }

                // Sample mode returns ReturnedCount
                if (root.TryGetProperty("ReturnedCount", out var returnedProp))
                {
                    var totalCount = root.GetProperty("TotalCount").GetInt32();
                    var returnedCount = returnedProp.GetInt32();
                    return $"Sampled {returnedCount} of {totalCount} listings";
                }

                // Stats mode
                var total = root.GetProperty("TotalCount").GetInt32();
                var activeCount = root.GetProperty("ActiveCount").GetInt32();
                var soldCount = root.GetProperty("SoldCount").GetInt32();
                var medianPrice = root.GetProperty("MedianPrice").GetDecimal();
                return $"{total} listings ({activeCount} active, {soldCount} sold) — median £{medianPrice:F2}";
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

            You have two tools:

            1. discover_variants — Clusters listings into product groups using TF-IDF
               + Ward hierarchical clustering. Returns clusters with stats (count,
               sell-through, median price, IQR, active/sold price split), a clean
               product label, and a suggested regex. Use this FIRST when the user asks
               about opportunities, variants, or what products exist.
               Parameters: threshold (default 1.5, lower = more clusters),
               condition (optional, pre-filter by condition), maxListings (default 2000).

            2. query_listings — Query listings and optionally apply filters to the UI.
               Parameters: regex, condition, minPrice, maxPrice, minDays, maxDays, status.
               Set apply=true to push filters to the UI (user sees results update live).
               Set mode="sample" to get individual listing rows instead of stats.
               In stats mode: returns counts, prices, sell-through, median/IQR,
               active vs sold median prices.
               In sample mode: returns individual titles, prices, statuses, URLs.
               Supports sortBy, sortDir, limit, randomise (sample mode only).

            Finding opportunities (preferred workflow):
            1. Call discover_variants to find product groups
            2. Pick the SINGLE best cluster (highest sell-through + tightest IQR + decent volume)
            3. Use the cluster's suggestedRegex with query_listings(regex=..., apply=true)
            4. Tell the user what you found with 1-2 sentences + key numbers
            5. Mention 1-2 runner-up variants briefly (one line each)
            6. Ask if they want to explore a runner-up or refine

            When discover_variants returns suggestedRegex for a cluster, use it directly
            with query_listings(regex=..., apply=true) instead of building regex from
            scratch. The suggested regex already excludes bundles/multipacks.

            When the user asks to see clusters, browse, or explore:
            - Just summarise the discover_variants output directly. DO NOT call
              query_listings for each cluster — that wastes tool calls and time.
            - List 5-8 clusters in a compact format: product label, condition,
              count, sell-through%, median price, active/sold price split. One line each.
            - The discover_variants result already has all the stats you need.
            - Only call query_listings when you need to TEST a specific regex
              or when the user picks a cluster to drill into.

            When the user asks "what else" or "what other clusters":
            - Show the NEXT 5-8 variants you haven't mentioned yet
            - Don't repeat variants you already recommended
            - If you've exhausted the good clusters, say so

            Never reference internal cluster IDs — use the product label from discover_variants.

            Filtering workflow (when user asks for specific filters):
            1. Build a regex pattern for what the user describes
            2. Use query_listings to test it — pass all filter params explicitly
            3. IMMEDIATELY apply with query_listings(apply=true) so the user can see results
            4. Tell the user what you found and ask if they want to refine

            Exploration workflow:
            When the user asks about the data, use query_listings(mode="sample") to look
            at actual listings. Choose the right sampling strategy:
            - randomise=true for a representative overview of what's in the data
            - sortBy=price, sortDir=desc to find expensive outliers
            - sortBy=price, sortDir=asc to find cheapest/best value
            - sortBy=daysOnMarket, sortDir=asc, status=Sold to see what sells fastest
            - sortBy=daysOnMarket, sortDir=desc to find stale unsold listings

            Condition pre-filtering:
            When the user asks to see only "Used" or "New" items, pass condition to
            discover_variants to avoid mixing conditions in clusters. This produces
            cleaner clusters with tighter price bands.

            Active vs Sold price split:
            Both discover_variants and query_listings return MedianActivePrice and
            MedianSoldPrice. Use these to identify arbitrage margins — when active
            price is below sold price, there's a flip opportunity.

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
            - ALWAYS use apply=true on query_listings when you have good filters.
              Never query more than once without applying. The user needs to see results.
            - Do NOT loop query_listings multiple times trying to refine before applying.
              Apply first, refine later if the user asks.
            - query_listings does NOT inherit UI filters — pass all filter params explicitly.

            Regex refinement guardrails:
            - You have a MAXIMUM of 10 tool calls per turn. Budget them wisely.
            - If your regex isn't producing the right results after 2 attempts, STOP
              guessing and use query_listings(mode="sample") to inspect actual listing
              titles. Read what's there, then build the regex from evidence.
            - Never make more than 3 query_listings calls in a row without applying.
              Blind regex tweaking wastes your budget and makes the user wait.
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

            Bundle/multipack exclusion:
            - ALWAYS exclude bundles and multipacks from single-item regex by default.
              Add ^(?!.*(x\s*\d|\d\s*x|bundle|lot|joblot|job lot|wholesale|case of))
              to the front of your regex. Single-item pricing is meaningless if bundles
              are mixed in.
            - If a listing title contains "x2", "x4", "2 x", "3x", etc., it is a
              multipack — your regex MUST exclude it unless the user asks for bundles.

            Communication style:
            - When recommending a specific flip: keep it SHORT. 1-2 sentences for
              the main pick + 1-2 runner-up lines. Apply the filter immediately.
            - When the user asks to explore or browse clusters: show up to 5-8
              variants in a compact list (product label + key numbers per line).
              This is NOT dumping raw data — it's a curated summary.
            - Lead with product label and key numbers, e.g.:
              "Best flip: Prismatic Evolutions ETB — 80% sell-through, median £131,
              active £126, sold £136. I've applied the filter."
            - Never dump raw cluster data or stats tables to the user.
            - Never reference internal cluster IDs — use the product label.
            - Don't narrate what you did ("I ran discovery...") — the user saw
              the tool calls stream in. Just give the finding.
            - Don't explain regex syntax unless asked.
            - Use plain language, not technical jargon.
            """);

        return sb.ToString();
    }

    private AIFunction BuildQueryListingsTool(int jobId, Action incrementToolCalls, Action<ChatFilterState> captureFilters)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Regex pattern to filter listing titles")] string? regex,
                [Description("Listing condition, e.g. 'New', 'Used'")] string? condition,
                [Description("Minimum price")] decimal? minPrice,
                [Description("Maximum price")] decimal? maxPrice,
                [Description("Minimum days on market")] int? minDays,
                [Description("Maximum days on market")] int? maxDays,
                [Description("Listing status: 'Active' or 'Sold'")] string? status,
                [Description("When true, pushes these filters to the UI so the user sees results update live. Default: false")] bool? apply,
                [Description("'stats' for aggregate stats + small sample, 'sample' for individual listing rows. Default: 'stats'")] string? mode,
                [Description("Column to sort by (sample mode only): 'title', 'price', 'listingStatus', 'condition', 'daysOnMarket'. Default: 'daysOnMarket'")] string? sortBy,
                [Description("Sort direction (sample mode only): 'asc' or 'desc'. Default: 'asc'")] string? sortDir,
                [Description("Number of listings to return in sample mode (1-100). Default: 10")] int? limit,
                [Description("If true, return a random sample instead of sorted order (sample mode only)")] bool? randomise
            ) =>
            {
                incrementToolCalls();

                if (apply == true)
                {
                    captureFilters(new ChatFilterState(regex, condition, minPrice, maxPrice, minDays, maxDays, status));
                }

                try
                {
                    if (mode == "sample")
                    {
                        var effectiveLimit = Math.Clamp(limit ?? 10, 1, 100);
                        var fetchSize = (randomise == true) ? 200 : effectiveLimit;

                        var sampleResult = await _queryService.Query(new ListingsQueryParams(
                            jobId, status, null, condition, minPrice, maxPrice,
                            minDays, maxDays, regex, sortBy ?? "daysOnMarket", sortDir ?? "asc", 1, fetchSize));

                        var items = sampleResult.Items;
                        if (randomise == true)
                        {
                            var rng = new Random();
                            items = items.OrderBy(_ => rng.Next()).Take(effectiveLimit);
                        }

                        var rows = items.Select(l => new QueryToolSample(
                            l.Title, l.Price?.ToString("F2"), l.ListingStatus, l.Condition, l.DaysOnMarket, l.Url));

                        return JsonSerializer.Serialize(new { TotalCount = sampleResult.TotalCount, ReturnedCount = effectiveLimit, Listings = rows });
                    }

                    // Stats mode (default)
                    var result = await _queryService.Query(new ListingsQueryParams(
                        jobId, status, null, condition, minPrice, maxPrice,
                        minDays, maxDays, regex, "daysOnMarket", "asc", 1, 200));

                    var allPrices = result.Items.Where(l => l.Price > 0).Select(l => l.Price!.Value).OrderBy(p => p).ToList();
                    var median = allPrices.Count > 0 ? allPrices[allPrices.Count / 2] : 0m;
                    var q1 = allPrices.Count > 0 ? allPrices[allPrices.Count / 4] : 0m;
                    var q3 = allPrices.Count > 0 ? allPrices[allPrices.Count * 3 / 4] : 0m;
                    var iqr = q3 - q1;

                    var activeStatPrices = result.Items
                        .Where(l => l.ListingStatus == "Active" && l.Price > 0)
                        .Select(l => l.Price!.Value).OrderBy(p => p).ToList();
                    var soldStatPrices = result.Items
                        .Where(l => l.ListingStatus is "Sold" or "Ended" && l.Price > 0)
                        .Select(l => l.Price!.Value).OrderBy(p => p).ToList();
                    var medianActive = activeStatPrices.Count > 0 ? activeStatPrices[activeStatPrices.Count / 2] : 0m;
                    var medianSold = soldStatPrices.Count > 0 ? soldStatPrices[soldStatPrices.Count / 2] : 0m;

                    var sample = result.Items.Take(10).Select(l => new QueryToolSample(
                        l.Title, l.Price?.ToString("F2"), l.ListingStatus, l.Condition, l.DaysOnMarket, l.Url));

                    return JsonSerializer.Serialize(new QueryToolResult(
                        result.TotalCount,
                        result.Stats.ActiveCount,
                        result.Stats.SoldCount,
                        result.Stats.SellThrough,
                        result.Stats.AvgDaysToSell,
                        median, iqr,
                        medianActive, medianSold,
                        result.Stats.MinPrice,
                        result.Stats.MaxPrice,
                        sample));
                }
                catch (RegexParseException)
                {
                    return JsonSerializer.Serialize(new { Error = "Invalid regex pattern. Fix the syntax and try again." });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "query_listings tool failed");
                    return JsonSerializer.Serialize(new { Error = "Query failed. Try a different filter." });
                }
            },
            "query_listings",
            "Query listings with filters. In stats mode (default): returns aggregate stats (counts, median price, IQR, active/sold price split) and a small sample. In sample mode: returns individual listing rows. Set apply=true to push filters to the UI.");
    }

    private static bool HasActiveFilters(ChatFilterState f) =>
        !string.IsNullOrWhiteSpace(f.Regex) ||
        !string.IsNullOrWhiteSpace(f.Condition) ||
        !string.IsNullOrWhiteSpace(f.Status) ||
        f.MinPrice.HasValue || f.MaxPrice.HasValue ||
        f.MinDays.HasValue || f.MaxDays.HasValue;

    private static readonly Dictionary<string, string[]> OptionalParams = new()
    {
        ["query_listings"] = ["apply", "mode", "sortBy", "sortDir", "limit", "randomise"],
        ["discover_variants"] = ["threshold", "condition", "maxListings"]
    };

    private static void FillOptionalDefaults(string toolName, Dictionary<string, object?> args)
    {
        if (!OptionalParams.TryGetValue(toolName, out var optionals))
        {
            return;
        }

        foreach (var param in optionals)
        {
            args.TryAdd(param, null);
        }
    }

    private static object? UnwrapJsonElement(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => je.GetRawText()
    };

    private AIFunction BuildDiscoverVariantsTool(int jobId, string searchTerm, Action incrementToolCalls)
    {
        return AIFunctionFactory.Create(
            async (
                [Description("Maximum number of listings to sample for clustering. Default 2000, max 3000.")] int? maxListings,
                [Description("Ward clustering threshold. Lower = more smaller clusters. Higher = fewer larger clusters. Omit to auto-scale based on dataset size. Only pass explicitly to override (range 0.5-3.0).")] double? threshold,
                [Description("Optional condition pre-filter. When set, only cluster listings matching this condition (e.g. 'New', 'Used').")] string? condition
            ) =>
            {
                incrementToolCalls();
                var sw = Stopwatch.StartNew();
                try
                {
                    var sampleSize = Math.Min(maxListings ?? 2000, 3000);

                    var query = _db.Listings
                        .Where(l => l.ScrapeJobId == jobId && l.Title != null && l.Price != null);

                    if (!string.IsNullOrWhiteSpace(condition))
                    {
                        query = query.Where(l => l.Condition == condition);
                    }

                    var listings = await query
                        .Select(l => new ClusterListingProjection(l.Id, l.Title, l.Price, l.ListingStatus, l.Condition))
                        .ToListAsync();

                    _logger.LogInformation("discover_variants: loaded {Count} listings for job {JobId}, condition={Condition}",
                        listings.Count, jobId, condition ?? "all");

                    if (listings.Count == 0)
                    {
                        return JsonSerializer.Serialize(new { Error = "No listings found for this job." });
                    }

                    var totalCount = listings.Count;
                    if (listings.Count > sampleSize)
                    {
                        var rng = new Random(42);
                        listings = listings.OrderBy(_ => rng.Next()).Take(sampleSize).ToList();
                    }

                    // Adaptive threshold: scale linearly with sample size to avoid over-fragmentation
                    var effectiveThreshold = threshold ?? Math.Max(1.5, listings.Count * 0.01);

                    _logger.LogInformation("discover_variants: clustering {SampleCount} titles, threshold={Threshold} (requested={Requested})",
                        listings.Count, effectiveThreshold, threshold?.ToString() ?? "auto");

                    var titles = listings.Select(l =>
                    {
                        var title = l.Title!;
                        if (!string.IsNullOrEmpty(l.Condition))
                        {
                            title += " [" + l.Condition.Replace("_", " ") + "]";
                        }
                        return title;
                    }).ToList();
                    var ids = listings.Select(l => l.Id).ToList();

                    _logger.LogInformation("discover_variants: TF-IDF + Ward clustering {Count} titles with threshold={Threshold}",
                        titles.Count, effectiveThreshold);
                    var clusterResult = _clusteringService.ClusterByText(ids, titles, effectiveThreshold);

                    var clusters = BuildVariantClusters(clusterResult, listings);

                    // Annotate clusters with LLM-generated labels and regex
                    var annotations = await AnnotateClusters(clusters, searchTerm, CancellationToken.None);
                    if (annotations.Count > 0)
                    {
                        clusters = clusters.Select(c =>
                            annotations.TryGetValue(c.ClusterId, out var a)
                                ? c with { Label = a.Label, SuggestedRegex = a.Regex }
                                : c).ToList();
                    }

                    sw.Stop();
                    _logger.LogInformation(
                        "discover_variants: found {ClusterCount} clusters from {Count} listings in {Elapsed:F1}s (threshold={Threshold}), annotated={Annotated}",
                        clusters.Count, listings.Count, sw.Elapsed.TotalSeconds, effectiveThreshold, annotations.Count);

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
                    return JsonSerializer.Serialize(new { Error = $"Clustering failed: {ex.Message}" });
                }
            },
            "discover_variants",
            "Discover product variants using TF-IDF + Ward hierarchical clustering. Returns clusters with stats, a clean product label, and a suggested regex. Use condition parameter to pre-filter by listing condition.");
    }

    private static readonly BinaryData SingleAnnotationSchema = BinaryData.FromString("""
        {
            "type": "object",
            "properties": {
                "label": { "type": "string", "description": "Clean product label, e.g. 'PS5 Digital Edition Console - White'" },
                "regex": { "type": "string", "description": "Case-insensitive regex to match this cluster's listings" }
            },
            "required": ["label", "regex"],
            "additionalProperties": false
        }
        """);

    private static readonly OpenAI.Chat.ChatResponseFormat SingleAnnotationFormat =
        OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(
            "cluster_annotation", SingleAnnotationSchema, jsonSchemaIsStrict: true);

    private static readonly JsonSerializerOptions AnnotationJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private async Task<IReadOnlyDictionary<int, ClusterAnnotation>> AnnotateClusters(
        IEnumerable<VariantCluster> clusters,
        string searchTerm,
        CancellationToken ct)
    {
        var clusterList = clusters.ToList();
        if (clusterList.Count == 0)
        {
            return new Dictionary<int, ClusterAnnotation>();
        }

        var tasks = clusterList.Select(c => AnnotateSingleCluster(c, searchTerm, ct));
        var results = await Task.WhenAll(tasks);

        var dict = new Dictionary<int, ClusterAnnotation>();
        foreach (var (clusterId, annotation) in results)
        {
            if (annotation != null)
            {
                dict[clusterId] = annotation;
            }
        }

        return dict;
    }

    private async Task<(int ClusterId, ClusterAnnotation? Annotation)> AnnotateSingleCluster(
        VariantCluster cluster,
        string searchTerm,
        CancellationToken ct)
    {
        var titles = string.Join(" | ", cluster.SampleTitles);

        var systemPrompt = $"""
            You are labeling a product cluster from an eBay search for "{searchTerm}".
            The cluster contains {cluster.Count} listings with condition: {cluster.Condition ?? "mixed"}.
            Sample titles: [{titles}]

            Generate:
            - label: A clean product name (brand + product + key differentiator, e.g. "PS5 Pro 2TB Console - White"). Do NOT include seller noise like "Free Shipping" or "Sealed".
            - regex: A case-insensitive regex that matches listings in this cluster. The regex is applied to listing TITLES only. Rules:
              - Do NOT require condition words (new, used, sealed, refurbished, pre-owned) — condition is filtered separately via a dedicated parameter, not from the title.
              - Do NOT require colour words unless colour is the key differentiator between clusters.
              - Use (?i) flag and .* between key terms since title word order varies.
              - Exclude bundles/multipacks with a negative lookahead: (?!.*\b(?:x\d+|lot|bundle|wholesale|pack)\b)
              - Keep the regex broad enough to match all listings in the cluster, not just the sample titles. Focus on the core product identity (brand + model + variant).
            """;

        try
        {
            var messages = new OpenAI.Chat.ChatMessage[]
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage("Label this cluster.")
            };

            var options = new ChatCompletionOptions
            {
                ResponseFormat = SingleAnnotationFormat
            };

            var completion = await _annotationClient.CompleteChatAsync(messages, options, ct);
            var json = completion.Value.Content[0].Text;

            var annotation = JsonSerializer.Deserialize<ClusterAnnotation>(json, AnnotationJsonOptions);
            if (annotation != null)
            {
                return (cluster.ClusterId, annotation with { ClusterId = cluster.ClusterId });
            }

            return (cluster.ClusterId, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Annotation failed for cluster {ClusterId} in search '{SearchTerm}'",
                cluster.ClusterId, searchTerm);
            return (cluster.ClusterId, null);
        }
    }

    private static List<VariantCluster> BuildVariantClusters(
        ClusteringResult clusterResult,
        List<ClusterListingProjection> listings)
    {
        var idToListing = listings.ToDictionary(l => l.Id);
        var clusters = new List<VariantCluster>();

        foreach (var cluster in clusterResult.Clusters.OrderByDescending(c => c.Items.Count))
        {
            if (cluster.Items.Count < 3)
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

            var activePrices = clusterListings
                .Where(l => l.ListingStatus == "Active" && l.Price.HasValue)
                .Select(l => l.Price!.Value)
                .OrderBy(p => p).ToList();
            var soldPrices = clusterListings
                .Where(l => l.ListingStatus is "Sold" or "Ended" && l.Price.HasValue)
                .Select(l => l.Price!.Value)
                .OrderBy(p => p).ToList();

            var medianActive = activePrices.Count > 0 ? activePrices[activePrices.Count / 2] : 0m;
            var medianSold = soldPrices.Count > 0 ? soldPrices[soldPrices.Count / 2] : 0m;

            // Dominant condition for the cluster
            var condition = clusterListings
                .Where(l => !string.IsNullOrEmpty(l.Condition))
                .GroupBy(l => l.Condition)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            // Sample titles: mix of sold and active
            var soldTitles = clusterListings
                .Where(l => l.ListingStatus == "Sold")
                .Take(2)
                .Select(l => l.Title!);
            var activeTitles = clusterListings
                .Where(l => l.ListingStatus == "Active")
                .Take(1)
                .Select(l => l.Title!);
            var sampleTitles = soldTitles.Concat(activeTitles).Take(3);

            clusters.Add(new VariantCluster(
                cluster.Label,
                clusterListings.Count,
                active, sold, st,
                median, iqr,
                medianActive, medianSold,
                prices.First(), prices.Last(),
                condition,
                null, null,
                sampleTitles));
        }

        return clusters;
    }
}
