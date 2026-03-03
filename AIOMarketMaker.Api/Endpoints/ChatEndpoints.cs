using System.Text.Json;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Api.Endpoints;

public record ChatApiRequest(
    string Message,
    IEnumerable<ChatHistoryEntry> History,
    ChatFilterState? CurrentFilters);

public record ChatApiResponse(string Message, ChatFilterState? Filters);

record ChatErrorResponse(string Error);

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        app.MapPost("/api/markets/{jobId:int}/chat", PostChat);
        app.MapPost("/api/markets/{jobId:int}/chat/stream", PostChatStream);
    }

    private static async Task<IResult> PostChat(
        int jobId,
        ChatApiRequest request,
        IMarketsChatService chatService,
        EtlDbContext db)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new ChatErrorResponse("Message is required"));
        }

        var job = await db.ScrapeJobs
            .Where(j => j.Id == jobId)
            .Select(j => new { j.SearchTerm })
            .FirstOrDefaultAsync();

        if (job == null)
        {
            return Results.NotFound(new ChatErrorResponse("Job not found"));
        }

        try
        {
            var result = await chatService.Chat(
                jobId,
                job.SearchTerm ?? $"Job {jobId}",
                new MarketsChatRequest(request.Message, request.History, request.CurrentFilters));

            return Results.Ok(new ChatApiResponse(result.Message, result.Filters));
        }
        catch (Exception)
        {
            return Results.Json(
                new ChatErrorResponse("AI service temporarily unavailable. Try again."),
                statusCode: 502);
        }
    }

    private static async Task PostChatStream(
        int jobId,
        HttpContext context,
        IMarketsChatService chatService,
        EtlDbContext db)
    {
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var request = await context.Request.ReadFromJsonAsync<ChatApiRequest>(jsonOptions);
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new ChatErrorResponse("Message is required"), jsonOptions);
            return;
        }

        var job = await db.ScrapeJobs
            .Where(j => j.Id == jobId)
            .Select(j => new { j.SearchTerm })
            .FirstOrDefaultAsync();

        if (job == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new ChatErrorResponse("Job not found"), jsonOptions);
            return;
        }

        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            var chatRequest = new MarketsChatRequest(request.Message, request.History, request.CurrentFilters);
            await foreach (var evt in chatService.ChatStream(jobId, job.SearchTerm ?? $"Job {jobId}", chatRequest, context.RequestAborted))
            {
                var json = JsonSerializer.Serialize(evt, jsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — nothing to write
        }
        catch (Exception)
        {
            try
            {
                var errorEvent = new ChatStreamEvent("error", new ChatErrorEvent("AI service temporarily unavailable. Try again."));
                var json = JsonSerializer.Serialize(errorEvent, jsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n");
                await context.Response.Body.FlushAsync();
            }
            catch
            {
                // Response stream is broken (client disconnected, etc.) — nothing to do
            }
        }
    }
}
