using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Api.Endpoints;

public record CategoryResponse(int Id, string Name, bool IsEnabled, int JobCount, DateTime CreatedUtc);
public record CreateCategoryRequest(string? Name);
public record UpdateCategoryRequest(string? Name);

public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/categories");
        group.MapGet("/", GetCategories);
        group.MapPost("/", CreateCategory);
        group.MapPut("/{id:int}", UpdateCategory);
        group.MapDelete("/{id:int}", DeleteCategory);
        group.MapPost("/{id:int}/enable", EnableCategory);
        group.MapPost("/{id:int}/disable", DisableCategory);
    }

    private static async Task<IResult> GetCategories(EtlDbContext db)
    {
        var categories = await db.Categories
            .Select(c => new CategoryResponse(
                c.Id, c.Name, c.IsEnabled,
                c.JobCategories.Count,
                c.CreatedUtc))
            .OrderBy(c => c.Name)
            .ToListAsync();

        return Results.Ok(categories);
    }

    private static async Task<IResult> CreateCategory(
        CreateCategoryRequest request, EtlDbContext db, ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("name is required"));
        }

        var exists = await db.Categories.AnyAsync(c => c.Name == request.Name);
        if (exists)
        {
            return Results.Conflict(new ErrorResponse($"Category '{request.Name}' already exists"));
        }

        var category = new Category
        {
            Name = request.Name.Trim(),
            CreatedUtc = DateTime.UtcNow
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync();

        logger.LogInformation("Created category {CategoryId}: '{Name}'", category.Id, category.Name);

        return Results.Created($"/api/categories/{category.Id}",
            new CategoryResponse(category.Id, category.Name, category.IsEnabled, 0, category.CreatedUtc));
    }

    private static async Task<IResult> UpdateCategory(
        int id, UpdateCategoryRequest request, EtlDbContext db, ILogger<Program> logger)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null)
        {
            return Results.NotFound(new ErrorResponse($"Category {id} not found"));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new ErrorResponse("name is required"));
        }

        var duplicate = await db.Categories.AnyAsync(c => c.Name == request.Name && c.Id != id);
        if (duplicate)
        {
            return Results.Conflict(new ErrorResponse($"Category '{request.Name}' already exists"));
        }

        category.Name = request.Name.Trim();
        await db.SaveChangesAsync();

        logger.LogInformation("Renamed category {CategoryId} to '{Name}'", id, category.Name);

        var jobCount = await db.JobCategories.CountAsync(jc => jc.CategoryId == id);
        return Results.Ok(new CategoryResponse(category.Id, category.Name, category.IsEnabled, jobCount, category.CreatedUtc));
    }

    private static async Task<IResult> DeleteCategory(
        int id, EtlDbContext db, ILogger<Program> logger)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null)
        {
            return Results.NotFound(new ErrorResponse($"Category {id} not found"));
        }

        db.Categories.Remove(category);
        await db.SaveChangesAsync();

        logger.LogInformation("Deleted category {CategoryId}: '{Name}'", id, category.Name);
        return Results.Ok(new MessageResponse($"Category '{category.Name}' deleted"));
    }

    private static async Task<IResult> EnableCategory(int id, EtlDbContext db)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null)
        {
            return Results.NotFound(new ErrorResponse($"Category {id} not found"));
        }

        category.IsEnabled = true;
        await db.SaveChangesAsync();

        var jobCount = await db.JobCategories.CountAsync(jc => jc.CategoryId == id);
        return Results.Ok(new CategoryResponse(category.Id, category.Name, category.IsEnabled, jobCount, category.CreatedUtc));
    }

    private static async Task<IResult> DisableCategory(int id, EtlDbContext db)
    {
        var category = await db.Categories.FindAsync(id);
        if (category == null)
        {
            return Results.NotFound(new ErrorResponse($"Category {id} not found"));
        }

        category.IsEnabled = false;
        await db.SaveChangesAsync();

        var jobCount = await db.JobCategories.CountAsync(jc => jc.CategoryId == id);
        return Results.Ok(new CategoryResponse(category.Id, category.Name, category.IsEnabled, jobCount, category.CreatedUtc));
    }
}
