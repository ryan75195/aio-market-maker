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
}
