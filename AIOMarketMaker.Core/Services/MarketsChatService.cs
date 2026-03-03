using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

        var toolMap = new Dictionary<string, AIFunction>
        {
            [queryTool.Name] = queryTool,
            [setFiltersTool.Name] = setFiltersTool,
            [sampleTool.Name] = sampleTool
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
            Tools = new List<AITool> { queryTool, setFiltersTool, sampleTool }
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

            You have three tools:

            1. query_listings — Test a filter combination. Returns aggregate stats (counts,
               prices, sell-through) and a small sample. Use this to check a regex before
               applying it.

            2. set_filters — Push filters to the UI. The user sees listings update live.
               Always call this after query_listings so the user sees results.

            3. sample_listings — Browse raw listing rows. Returns individual titles, prices,
               statuses, conditions, days on market, and URLs. Use this to explore the data,
               spot patterns, check outliers, or understand what's in the dataset. Supports
               sorting by any column, limiting results, and random sampling.

            Filtering workflow:
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

            Finding opportunities:
            A broad search category contains many different product variants. The user wants
            to find specific variants worth buying and reselling. Your job is to drill down
            to the leaf-level variant — the most specific product you can isolate. Follow
            this approach:
            1. Sample with randomise=true to see what product variants exist in the data
            2. Identify distinct sub-categories from the titles (e.g. specific models,
               editions, sizes, colourways). These are the variants
            3. For each promising variant, query_listings with a targeted regex to get its
               stats (sell-through, avg sold price, volume)
            4. Check if the variant is truly a single product: if the price spread is wide
               (e.g. min £50, max £300), the regex is probably catching multiple sub-variants.
               Sample the variant's listings to see what's in there, then tighten the regex
               to isolate the actual leaf product (e.g. a specific edition, size, or model)
            5. Compare leaf variants: high sell-through (>50%) + tight price clustering +
               decent volume = good opportunity. Low sell-through or wide spread = risky
            6. Apply the best variant's filter with set_filters so the user sees it
            7. Give a specific recommendation: "X variant sells at £Y median with Z%
               sell-through. Buy under £A for reliable margin."

            Keep drilling until the price spread is tight. If a filtered set has a wide
            price range, it means you're looking at mixed products — sample the listings
            and split them further. A good leaf variant will have most sold prices within
            a 20-30% band of the median. For example, if the median sold price is £100,
            most sales should fall between £85-£115. If you see clusters at £60 AND £150,
            those are two different products sharing a title pattern.

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
}
