# Job Categories Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a many-to-many category tagging system for scrape jobs with enable/disable at the category level, changing which jobs are "effectively enabled" for scraping.

**Architecture:** New `Categories` and `JobCategories` tables. New `CategoryEndpoints` for CRUD. Modified `JobEndpoints` to include categories in responses. Modified scrape filtering in `ScrapeEndpoints` and `NightlyScrapeService` to use effective-enabled logic. Electron UI gains a Jobs/Categories view toggle in the Job Overview panel.

**Tech Stack:** .NET 8.0, EF Core, SQL Server LocalDB, Vue 3 Options API (Electron desktop)

**Design doc:** `docs/plans/2026-02-16-job-categories-design.md`

---

### Task 1: Database Migration — Create Categories and JobCategories Tables

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/039_CreateCategoriesAndJobCategories.sql`

**Step 1: Write the migration SQL**

```sql
-- Migration: 039_CreateCategoriesAndJobCategories
-- Description: Create Categories table and JobCategories join table for many-to-many job tagging
-- Date: 2026-02-16

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Categories')
BEGIN
    CREATE TABLE Categories (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(100) NOT NULL,
        IsEnabled BIT NOT NULL CONSTRAINT DF_Categories_IsEnabled DEFAULT 1,
        CreatedUtc DATETIME2 NOT NULL CONSTRAINT DF_Categories_CreatedUtc DEFAULT SYSUTCDATETIME()
    );
END

GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'UQ_Categories_Name')
BEGIN
    CREATE UNIQUE INDEX UQ_Categories_Name ON Categories (Name);
END

GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'JobCategories')
BEGIN
    CREATE TABLE JobCategories (
        JobId INT NOT NULL,
        CategoryId INT NOT NULL,
        CONSTRAINT PK_JobCategories PRIMARY KEY (JobId, CategoryId),
        CONSTRAINT FK_JobCategories_ScrapeJobs FOREIGN KEY (JobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE,
        CONSTRAINT FK_JobCategories_Categories FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
    );
END

GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_JobCategories_CategoryId')
BEGIN
    CREATE INDEX IX_JobCategories_CategoryId ON JobCategories (CategoryId);
END
```

**Step 2: Rebuild Core project to embed migration**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Migrations/SqlServer/039_CreateCategoriesAndJobCategories.sql
git commit -m "feat: add migration 039 for Categories and JobCategories tables"
```

---

### Task 2: EF Core Model and DbContext Configuration

**Files:**
- Create: `AIOMarketMaker.Core/Data/Models/Category.cs`
- Create: `AIOMarketMaker.Core/Data/Models/JobCategory.cs`
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeJob.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

**Step 1: Create the Category model**

Create `AIOMarketMaker.Core/Data/Models/Category.cs`:

```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public ICollection<JobCategory> JobCategories { get; set; } = new List<JobCategory>();
}
```

**Step 2: Create the JobCategory join entity**

Create `AIOMarketMaker.Core/Data/Models/JobCategory.cs`:

```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class JobCategory
{
    public int JobId { get; set; }
    public ScrapeJob Job { get; set; } = null!;
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
}
```

**Step 3: Add navigation property to ScrapeJob**

In `AIOMarketMaker.Core/Data/Models/ScrapeJob.cs`, add at the end of the class (before the closing brace):

```csharp
public ICollection<JobCategory> JobCategories { get; set; } = new List<JobCategory>();
```

**Step 4: Add DbSets and configure entities in EtlDbContext**

In `AIOMarketMaker.Core/Data/EtlDbContext.cs`:

Add DbSets after the existing ones (around line 26):
```csharp
public DbSet<Category> Categories { get; set; } = null!;
public DbSet<JobCategory> JobCategories { get; set; } = null!;
```

Add entity configuration inside `OnModelCreating` (after the ScrapeJob configuration, around line 52):

```csharp
modelBuilder.Entity<Category>(entity =>
{
    entity.ToTable("Categories");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
    entity.HasIndex(e => e.Name).IsUnique();
    entity.Property(e => e.IsEnabled).HasDefaultValue(true);
    entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);
});

modelBuilder.Entity<JobCategory>(entity =>
{
    entity.ToTable("JobCategories");
    entity.HasKey(e => new { e.JobId, e.CategoryId });

    entity.HasOne(e => e.Job)
        .WithMany(j => j.JobCategories)
        .HasForeignKey(e => e.JobId)
        .OnDelete(DeleteBehavior.Cascade);

    entity.HasOne(e => e.Category)
        .WithMany(c => c.JobCategories)
        .HasForeignKey(e => e.CategoryId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

**Step 5: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Models/Category.cs AIOMarketMaker/AIOMarketMaker.Core/Data/Models/JobCategory.cs AIOMarketMaker/AIOMarketMaker.Core/Data/Models/ScrapeJob.cs AIOMarketMaker/AIOMarketMaker.Core/Data/EtlDbContext.cs
git commit -m "feat: add Category and JobCategory EF Core models and DbContext config"
```

---

### Task 3: Category API Endpoints

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/CategoryEndpoints.cs`
- Modify: `AIOMarketMaker.Api/Program.cs` (register endpoints)

**Step 1: Write the failing test**

Create `AIOMarketMaker.Tests/Unit/Endpoints/CategoryEndpoints_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class CategoryEndpoints_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new EtlDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_create_category()
    {
        var category = new Category { Name = "Electronics" };
        _db.Categories.Add(category);
        await _db.SaveChangesAsync();

        var saved = await _db.Categories.FirstOrDefaultAsync(c => c.Name == "Electronics");
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.IsEnabled, Is.True);
    }

    [Test]
    public async Task Should_assign_job_to_category()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        var cat = new Category { Name = "Electronics" };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var loaded = await _db.JobCategories
            .Include(jc => jc.Category)
            .Where(jc => jc.JobId == job.Id)
            .ToListAsync();

        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].Category.Name, Is.EqualTo("Electronics"));
    }

    [Test]
    public async Task Should_delete_category_without_deleting_jobs()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        var cat = new Category { Name = "Electronics" };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        _db.Categories.Remove(cat);
        await _db.SaveChangesAsync();

        var jobStillExists = await _db.ScrapeJobs.FindAsync(job.Id);
        Assert.That(jobStillExists, Is.Not.Null);

        var links = await _db.JobCategories.Where(jc => jc.JobId == job.Id).ToListAsync();
        Assert.That(links, Is.Empty);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~CategoryEndpoints_UnitTests"`
Expected: FAIL (no DbSets exist yet if you haven't done Task 2, or PASS if Task 2 is complete — these test the model layer)

**Step 3: Create CategoryEndpoints.cs**

Create `AIOMarketMaker.Api/Endpoints/CategoryEndpoints.cs`:

```csharp
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
```

**Step 4: Register endpoints in Program.cs**

Find where `app.MapJobEndpoints()` is called in `AIOMarketMaker.Api/Program.cs` and add below it:

```csharp
app.MapCategoryEndpoints();
```

**Step 5: Build and run tests**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~CategoryEndpoints_UnitTests"`
Expected: All 3 tests pass

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/CategoryEndpoints.cs AIOMarketMaker/AIOMarketMaker.Api/Program.cs AIOMarketMaker/AIOMarketMaker.Tests/Unit/Endpoints/CategoryEndpoints_UnitTests.cs
git commit -m "feat: add Category CRUD API endpoints"
```

---

### Task 4: Modify Job Endpoints — Include Categories in Responses and Accept CategoryIds

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/JobEndpoints.cs`

**Step 1: Update the response record and request records**

Replace the records at the top of `JobEndpoints.cs`:

```csharp
public record CreateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled, IEnumerable<int>? CategoryIds);
public record UpdateJobRequest(string? SearchTerm, string? FilterInstructions, bool? IsEnabled, IEnumerable<int>? CategoryIds);
public record JobCategoryInfo(int Id, string Name);
public record JobResponse(int Id, string SearchTerm, string? FilterInstructions, bool IsEnabled, DateTime? LastRunUtc, DateTime CreatedUtc, IEnumerable<JobCategoryInfo> Categories);
public record JobToggleResponse(int Id, string SearchTerm, bool IsEnabled);
public record ErrorResponse(string Error);
public record MessageResponse(string Message);
```

**Step 2: Add a new endpoint for setting job categories**

In `MapJobEndpoints`, add:

```csharp
group.MapPost("/{id:int}/categories", SetJobCategories);
```

**Step 3: Update GetJobs to include categories**

Replace the `GetJobs` method body. Key change: load job categories and include them in the response.

```csharp
private static async Task<IResult> GetJobs(EtlDbContext db)
{
    var lastRunByJob = await db.ScrapeRuns
        .GroupBy(r => r.JobId)
        .Select(g => new { JobId = g.Key, LastRun = g.Max(r => r.StartedUtc) })
        .ToDictionaryAsync(x => x.JobId, x => x.LastRun);

    var jobCategories = await db.JobCategories
        .Include(jc => jc.Category)
        .GroupBy(jc => jc.JobId)
        .Select(g => new { JobId = g.Key, Categories = g.Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name)).ToList() })
        .ToDictionaryAsync(x => x.JobId, x => x.Categories);

    var jobs = await db.ScrapeJobs
        .Select(j => new { j.Id, j.SearchTerm, j.FilterInstructions, j.IsEnabled, j.CreatedUtc })
        .ToListAsync();

    var result = jobs.Select(j => new JobResponse(
        j.Id, j.SearchTerm, j.FilterInstructions, j.IsEnabled,
        lastRunByJob.GetValueOrDefault(j.Id),
        j.CreatedUtc,
        jobCategories.GetValueOrDefault(j.Id, new List<JobCategoryInfo>())));

    return Results.Ok(result);
}
```

**Step 4: Update GetJob to include categories**

```csharp
private static async Task<IResult> GetJob(int id, EtlDbContext db)
{
    var job = await db.ScrapeJobs.FindAsync(id);
    if (job == null)
    {
        return Results.NotFound(new ErrorResponse($"Job {id} not found"));
    }

    var categories = await db.JobCategories
        .Where(jc => jc.JobId == id)
        .Include(jc => jc.Category)
        .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
        .ToListAsync();

    return Results.Ok(new JobResponse(
        job.Id, job.SearchTerm, job.FilterInstructions,
        job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
}
```

**Step 5: Update CreateJob to accept categoryIds**

After `db.SaveChangesAsync()` for the new job, add category assignment:

```csharp
if (request.CategoryIds != null)
{
    foreach (var catId in request.CategoryIds.Distinct())
    {
        if (await db.Categories.AnyAsync(c => c.Id == catId))
        {
            db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catId });
        }
    }
    await db.SaveChangesAsync();
}

var categories = await db.JobCategories
    .Where(jc => jc.JobId == job.Id)
    .Include(jc => jc.Category)
    .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
    .ToListAsync();
```

Update the return to include categories:

```csharp
return Results.Created($"/api/jobs/{job.Id}", new JobResponse(
    job.Id, job.SearchTerm, job.FilterInstructions,
    job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
```

**Step 6: Update UpdateJob to accept categoryIds**

After the existing property updates but before `SaveChangesAsync`, add:

```csharp
if (request.CategoryIds != null)
{
    var existing = await db.JobCategories.Where(jc => jc.JobId == id).ToListAsync();
    db.JobCategories.RemoveRange(existing);

    foreach (var catId in request.CategoryIds.Distinct())
    {
        if (await db.Categories.AnyAsync(c => c.Id == catId))
        {
            db.JobCategories.Add(new JobCategory { JobId = id, CategoryId = catId });
        }
    }
}
```

And update the return to include categories:

```csharp
var categories = await db.JobCategories
    .Where(jc => jc.JobId == id)
    .Include(jc => jc.Category)
    .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
    .ToListAsync();

return Results.Ok(new JobResponse(
    job.Id, job.SearchTerm, job.FilterInstructions,
    job.IsEnabled, job.LastRunUtc, job.CreatedUtc, categories));
```

**Step 7: Add SetJobCategories method**

```csharp
private static async Task<IResult> SetJobCategories(
    int id, IEnumerable<int> categoryIds, EtlDbContext db)
{
    var job = await db.ScrapeJobs.FindAsync(id);
    if (job == null)
    {
        return Results.NotFound(new ErrorResponse($"Job {id} not found"));
    }

    var existing = await db.JobCategories.Where(jc => jc.JobId == id).ToListAsync();
    db.JobCategories.RemoveRange(existing);

    foreach (var catId in categoryIds.Distinct())
    {
        if (await db.Categories.AnyAsync(c => c.Id == catId))
        {
            db.JobCategories.Add(new JobCategory { JobId = id, CategoryId = catId });
        }
    }

    await db.SaveChangesAsync();

    var categories = await db.JobCategories
        .Where(jc => jc.JobId == id)
        .Include(jc => jc.Category)
        .Select(jc => new JobCategoryInfo(jc.Category.Id, jc.Category.Name))
        .ToListAsync();

    return Results.Ok(categories);
}
```

**Step 8: Add missing using for JobCategory model**

Ensure `using AIOMarketMaker.Core.Data.Models;` is at the top of `JobEndpoints.cs`.

**Step 9: Build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

**Step 10: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/JobEndpoints.cs
git commit -m "feat: include categories in job API responses and accept categoryIds on create/update"
```

---

### Task 5: Effective-Enabled Scrape Filtering

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs:26`
- Modify: `AIOMarketMaker.Api/Services/NightlyScrapeService.cs:59`
- Test: `AIOMarketMaker.Tests/Unit/Services/EffectiveEnabled_UnitTests.cs`

**Step 1: Write failing tests for effective-enabled logic**

Create `AIOMarketMaker.Tests/Unit/Services/EffectiveEnabled_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class EffectiveEnabled_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new EtlDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task Should_include_uncategorized_enabled_job()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "PS5", IsEnabled = true });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_uncategorized_disabled_job()
    {
        _db.ScrapeJobs.Add(new ScrapeJob { SearchTerm = "PS5", IsEnabled = false });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Is.Empty);
    }

    [Test]
    public async Task Should_include_job_with_one_enabled_category()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = true };
        var cat = new Category { Name = "Electronics", IsEnabled = true };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_job_with_all_categories_disabled()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = true };
        var cat = new Category { Name = "Electronics", IsEnabled = false };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Is.Empty);
    }

    [Test]
    public async Task Should_include_job_when_any_category_is_enabled()
    {
        var job = new ScrapeJob { SearchTerm = "Rolex", IsEnabled = true };
        var catEnabled = new Category { Name = "Watches", IsEnabled = true };
        var catDisabled = new Category { Name = "Luxury", IsEnabled = false };
        _db.ScrapeJobs.Add(job);
        _db.Categories.AddRange(catEnabled, catDisabled);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catEnabled.Id });
        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = catDisabled.Id });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Should_exclude_job_when_job_disabled_even_if_category_enabled()
    {
        var job = new ScrapeJob { SearchTerm = "PS5", IsEnabled = false };
        var cat = new Category { Name = "Electronics", IsEnabled = true };
        _db.ScrapeJobs.Add(job);
        _db.Categories.Add(cat);
        await _db.SaveChangesAsync();

        _db.JobCategories.Add(new JobCategory { JobId = job.Id, CategoryId = cat.Id });
        await _db.SaveChangesAsync();

        var jobs = await GetEffectivelyEnabledJobs();
        Assert.That(jobs, Is.Empty);
    }

    /// <summary>
    /// This is the LINQ query that ScrapeEndpoints and NightlyScrapeService will both use.
    /// Extract this into a shared method if the query works.
    /// </summary>
    private async Task<List<ScrapeJob>> GetEffectivelyEnabledJobs()
    {
        return await _db.ScrapeJobs
            .Where(j => j.IsEnabled
                && (!j.JobCategories.Any() || j.JobCategories.Any(jc => jc.Category.IsEnabled)))
            .ToListAsync();
    }
}
```

**Step 2: Run tests to verify they pass** (tests validate the query against in-memory provider)

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~EffectiveEnabled_UnitTests"`
Expected: All 6 tests pass

**Step 3: Extract the query into a shared extension method**

Create `AIOMarketMaker.Core/Data/ScrapeJobQueryExtensions.cs`:

```csharp
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Data;

public static class ScrapeJobQueryExtensions
{
    public static IQueryable<ScrapeJob> WhereEffectivelyEnabled(this IQueryable<ScrapeJob> query)
    {
        return query.Where(j => j.IsEnabled
            && (!j.JobCategories.Any() || j.JobCategories.Any(jc => jc.Category.IsEnabled)));
    }
}
```

**Step 4: Update ScrapeEndpoints.cs** — line 26

Replace:
```csharp
var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
```
With:
```csharp
var jobs = await db.ScrapeJobs.WhereEffectivelyEnabled()
```

**Step 5: Update NightlyScrapeService.cs** — line 59

Replace:
```csharp
var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
```
With:
```csharp
var jobs = await db.ScrapeJobs.WhereEffectivelyEnabled()
```

Add `using AIOMarketMaker.Core.Data;` to both files if not already present.

**Step 6: Update the test to use the extension method**

In `EffectiveEnabled_UnitTests.cs`, replace the `GetEffectivelyEnabledJobs` method:

```csharp
private async Task<List<ScrapeJob>> GetEffectivelyEnabledJobs()
{
    return await _db.ScrapeJobs.WhereEffectivelyEnabled().ToListAsync();
}
```

**Step 7: Build and run tests**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~EffectiveEnabled_UnitTests"`
Expected: All 6 tests pass

**Step 8: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/ScrapeJobQueryExtensions.cs AIOMarketMaker/AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs AIOMarketMaker/AIOMarketMaker.Api/Services/NightlyScrapeService.cs AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/EffectiveEnabled_UnitTests.cs
git commit -m "feat: replace IsEnabled filter with effective-enabled logic (job + category)"
```

---

### Task 6: UI — View Toggle and Job View Categories Column

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Add category state to app.js data()**

Add these properties to the `data()` return object (near `showJobsPanel`, around line 27):

```javascript
jobOverviewMode: 'jobs',  // 'jobs' or 'categories'
categories: [],
expandedCategories: {},
newCategoryName: '',
showNewCategoryForm: false,
categorySearch: '',
```

**Step 2: Add category API methods to app.js methods**

```javascript
async loadCategories() {
  try {
    const data = await this.apiCall('/categories');
    this.categories = this.toCamelCase(data);
  } catch (err) {
    this.showToast(`Failed to load categories: ${err.message}`, 'error');
  }
},

async createCategory() {
  if (!this.newCategoryName.trim()) { return; }
  try {
    const data = await this.apiCall('/categories', {
      method: 'POST',
      body: JSON.stringify({ name: this.newCategoryName.trim() })
    });
    this.categories.push(this.toCamelCase(data));
    this.newCategoryName = '';
    this.showNewCategoryForm = false;
    this.showToast('Category created', 'success');
  } catch (err) {
    this.showToast(`Failed to create category: ${err.message}`, 'error');
  }
},

async renameCategory(cat) {
  const newName = prompt('Rename category:', cat.name);
  if (!newName || newName.trim() === cat.name) { return; }
  try {
    const data = await this.apiCall(`/categories/${cat.id}`, {
      method: 'PUT',
      body: JSON.stringify({ name: newName.trim() })
    });
    const updated = this.toCamelCase(data);
    const idx = this.categories.findIndex(c => c.id === cat.id);
    if (idx !== -1) { this.categories[idx] = updated; }
    this.showToast('Category renamed', 'success');
  } catch (err) {
    this.showToast(`Failed to rename: ${err.message}`, 'error');
  }
},

async deleteCategory(cat) {
  if (!confirm(`Delete category "${cat.name}"? Jobs will NOT be deleted.`)) { return; }
  try {
    await this.apiCall(`/categories/${cat.id}`, { method: 'DELETE' });
    this.categories = this.categories.filter(c => c.id !== cat.id);
    this.showToast('Category deleted', 'success');
    await this.loadJobs();
  } catch (err) {
    this.showToast(`Failed to delete: ${err.message}`, 'error');
  }
},

async toggleCategory(cat) {
  try {
    const endpoint = cat.isEnabled ? `/categories/${cat.id}/disable` : `/categories/${cat.id}/enable`;
    const data = await this.apiCall(endpoint, { method: 'POST' });
    const updated = this.toCamelCase(data);
    const idx = this.categories.findIndex(c => c.id === cat.id);
    if (idx !== -1) { this.categories[idx] = updated; }
    this.showToast(`Category ${updated.isEnabled ? 'enabled' : 'disabled'}`, 'success');
  } catch (err) {
    this.showToast(`Failed to toggle category: ${err.message}`, 'error');
  }
},

toggleCategoryExpand(catId) {
  this.expandedCategories[catId] = !this.expandedCategories[catId];
},

jobsInCategory(catId) {
  return this.jobs.filter(j => j.categories?.some(c => c.id === catId));
},

uncategorizedJobs() {
  return this.jobs.filter(j => !j.categories || j.categories.length === 0);
},

async removeJobFromCategory(jobId, catId) {
  const job = this.jobs.find(j => j.id === jobId);
  if (!job) { return; }
  const newCatIds = (job.categories || []).filter(c => c.id !== catId).map(c => c.id);
  try {
    await this.apiCall(`/jobs/${jobId}/categories`, {
      method: 'POST',
      body: JSON.stringify(newCatIds)
    });
    job.categories = job.categories.filter(c => c.id !== catId);
    // Update category job count
    const cat = this.categories.find(c => c.id === catId);
    if (cat) { cat.jobCount = Math.max(0, cat.jobCount - 1); }
    this.showToast('Job removed from category', 'success');
  } catch (err) {
    this.showToast(`Failed to remove: ${err.message}`, 'error');
  }
},

async addJobToCategory(jobId, catId) {
  const job = this.jobs.find(j => j.id === jobId);
  if (!job) { return; }
  const currentCatIds = (job.categories || []).map(c => c.id);
  if (currentCatIds.includes(catId)) { return; }
  try {
    await this.apiCall(`/jobs/${jobId}/categories`, {
      method: 'POST',
      body: JSON.stringify([...currentCatIds, catId])
    });
    const cat = this.categories.find(c => c.id === catId);
    if (cat) {
      job.categories = [...(job.categories || []), { id: cat.id, name: cat.name }];
      cat.jobCount = (cat.jobCount || 0) + 1;
    }
    this.showToast('Job added to category', 'success');
  } catch (err) {
    this.showToast(`Failed to add: ${err.message}`, 'error');
  }
},
```

**Step 3: Update loadJobs call in Job Overview button**

Update the Job Overview button click handler to also load categories:

In `index.html`, change:
```html
<button class="btn" @click="showJobsPanel = true; loadJobs()">Job Overview</button>
```
To:
```html
<button class="btn" @click="showJobsPanel = true; loadJobs(); loadCategories()">Job Overview</button>
```

**Step 4: Add a computed for filtered categories**

In `app.js` computed section:

```javascript
filteredCategories() {
  if (!this.categorySearch) { return this.categories; }
  const q = this.categorySearch.toLowerCase();
  return this.categories.filter(c => c.name.toLowerCase().includes(q));
},
```

**Step 5: Update Job Overview toolbar in index.html**

Replace the existing `jobs-toolbar` div (around line 208-212) with:

```html
<div class="jobs-toolbar">
  <div class="view-toggle">
    <button class="btn small" :class="{ active: jobOverviewMode === 'jobs' }" @click="jobOverviewMode = 'jobs'">Jobs</button>
    <button class="btn small" :class="{ active: jobOverviewMode === 'categories' }" @click="jobOverviewMode = 'categories'">Categories</button>
  </div>
  <template v-if="jobOverviewMode === 'jobs'">
    <button class="btn primary small" @click="showJobForm = true">+ New Job</button>
    <input type="text" v-model="jobSearch" @input="jobPage = 1" class="search-input" placeholder="Search jobs...">
    <span class="count-badge">{{ filteredJobs.length }} of {{ jobs.length }} jobs</span>
  </template>
  <template v-else>
    <button class="btn primary small" @click="showNewCategoryForm = true">+ New Category</button>
    <input type="text" v-model="categorySearch" class="search-input" placeholder="Search categories...">
    <span class="count-badge">{{ filteredCategories.length }} categories</span>
  </template>
</div>
```

**Step 6: Add Categories column to job table**

In the Job View table `<thead>`, add after the Search Term column:
```html
<th>Categories</th>
```

In the `<tbody>` rows, add after the searchTerm `<td>`:
```html
<td>
  <span v-for="cat in job.categories" :key="cat.id" class="tag-pill">{{ cat.name }}</span>
  <span v-if="!job.categories || job.categories.length === 0" class="muted-text">-</span>
</td>
```

Update the empty row colspan from 5 to 6:
```html
<td colspan="6" class="empty">
```

**Step 7: Add Category View template**

After the Job View table/pagination `</template>` and before `<!-- Batch list content -->`, add the Category View:

```html
<!-- Category View -->
<template v-if="jobOverviewMode === 'categories'">
  <!-- New Category inline form -->
  <div v-if="showNewCategoryForm" class="new-category-form">
    <input type="text" v-model="newCategoryName" placeholder="Category name" @keyup.enter="createCategory" autofocus>
    <button class="btn primary small" @click="createCategory">Create</button>
    <button class="btn small" @click="showNewCategoryForm = false; newCategoryName = ''">Cancel</button>
  </div>

  <div v-for="cat in filteredCategories" :key="cat.id" class="category-accordion">
    <div class="category-header" @click="toggleCategoryExpand(cat.id)">
      <span class="expand-icon">{{ expandedCategories[cat.id] ? '\u25BC' : '\u25B6' }}</span>
      <span class="category-name">{{ cat.name }}</span>
      <span class="count-badge small">{{ cat.jobCount }} jobs</span>
      <button class="btn small" :class="cat.isEnabled ? 'success' : 'muted'" @click.stop="toggleCategory(cat)">
        {{ cat.isEnabled ? 'Enabled' : 'Disabled' }}
      </button>
      <button class="btn small" @click.stop="renameCategory(cat)">Rename</button>
      <button class="btn small danger" @click.stop="deleteCategory(cat)">Delete</button>
    </div>
    <div v-if="expandedCategories[cat.id]" class="category-jobs">
      <table class="data-table compact">
        <thead>
          <tr>
            <th>ID</th>
            <th>Search Term</th>
            <th>Enabled</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="job in jobsInCategory(cat.id)" :key="job.id">
            <td class="number">{{ job.id }}</td>
            <td>{{ job.searchTerm }}</td>
            <td>
              <button class="btn small" :class="job.isEnabled ? 'success' : 'muted'" @click="toggleJob(job)">
                {{ job.isEnabled ? 'Enabled' : 'Disabled' }}
              </button>
            </td>
            <td>
              <button class="btn small danger" @click="removeJobFromCategory(job.id, cat.id)">Remove</button>
            </td>
          </tr>
          <tr v-if="jobsInCategory(cat.id).length === 0">
            <td colspan="4" class="empty">No jobs in this category</td>
          </tr>
        </tbody>
      </table>
      <div class="add-job-row">
        <select @change="addJobToCategory(Number($event.target.value), cat.id); $event.target.value = ''">
          <option value="" disabled selected>+ Add job...</option>
          <option v-for="j in jobs.filter(j => !(j.categories || []).some(c => c.id === cat.id))" :key="j.id" :value="j.id">
            {{ j.searchTerm }}
          </option>
        </select>
      </div>
    </div>
  </div>

  <!-- Uncategorized section -->
  <div class="category-accordion" v-if="uncategorizedJobs().length > 0">
    <div class="category-header" @click="toggleCategoryExpand('uncategorized')">
      <span class="expand-icon">{{ expandedCategories['uncategorized'] ? '\u25BC' : '\u25B6' }}</span>
      <span class="category-name muted-text">Uncategorized</span>
      <span class="count-badge small">{{ uncategorizedJobs().length }} jobs</span>
    </div>
    <div v-if="expandedCategories['uncategorized']" class="category-jobs">
      <table class="data-table compact">
        <thead>
          <tr>
            <th>ID</th>
            <th>Search Term</th>
            <th>Enabled</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="job in uncategorizedJobs()" :key="job.id">
            <td class="number">{{ job.id }}</td>
            <td>{{ job.searchTerm }}</td>
            <td>
              <button class="btn small" :class="job.isEnabled ? 'success' : 'muted'" @click="toggleJob(job)">
                {{ job.isEnabled ? 'Enabled' : 'Disabled' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>

  <div v-if="filteredCategories.length === 0 && uncategorizedJobs().length === 0" class="empty-state">
    No categories yet. Click "+ New Category" to create one.
  </div>
</template>
```

**Step 8: Wrap existing Job View table in a conditional**

Wrap the existing `<table class="data-table">` and its pagination in:
```html
<template v-if="jobOverviewMode === 'jobs'">
  <!-- existing table and pagination here -->
</template>
```

**Step 9: Add category multi-select to job edit modal**

In the job form modal (around line 601-609 in index.html), add after the Filter Instructions textarea:

```html
<div class="form-group" v-if="categories.length > 0">
  <label>Categories</label>
  <div class="category-checkboxes">
    <label v-for="cat in categories" :key="cat.id" class="checkbox-label">
      <input type="checkbox" :value="cat.id" v-model="jobForm.categoryIds"> {{ cat.name }}
    </label>
  </div>
</div>
```

Update `jobForm` in `data()`:
```javascript
jobForm: {
  searchTerm: '',
  filterInstructions: '',
  isEnabled: true,
  categoryIds: []
},
```

Update `editJob` method to populate categoryIds:
```javascript
editJob(job) {
  this.editingJob = job;
  this.jobForm = {
    searchTerm: job.searchTerm,
    filterInstructions: job.filterInstructions || '',
    isEnabled: job.isEnabled,
    categoryIds: (job.categories || []).map(c => c.id)
  };
  this.showJobForm = true;
},
```

Update `closeJobForm` to reset categoryIds:
```javascript
closeJobForm() {
  this.showJobForm = false;
  this.editingJob = null;
  this.jobForm = { searchTerm: '', filterInstructions: '', isEnabled: true, categoryIds: [] };
},
```

Update `saveJob` to send categoryIds in both create and update:
- The existing `JSON.stringify(this.jobForm)` already includes `categoryIds` since it's part of the form object
- After saving, reload jobs and categories:

Add after `this.closeJobForm()` in `saveJob`:
```javascript
await this.loadJobs();
await this.loadCategories();
```

**Step 10: Add CSS styles**

In `styles.css`, add:

```css
.view-toggle {
  display: flex;
  gap: 0;
  margin-right: 12px;
}
.view-toggle .btn { border-radius: 0; }
.view-toggle .btn:first-child { border-radius: 4px 0 0 4px; }
.view-toggle .btn:last-child { border-radius: 0 4px 4px 0; }
.view-toggle .btn.active { background: #4a9eff; color: #fff; }

.tag-pill {
  display: inline-block;
  background: #2a3a4a;
  color: #8cc5ff;
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 0.75em;
  margin: 1px 2px;
}

.category-accordion { margin-bottom: 1px; }
.category-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  background: #1e1e1e;
  cursor: pointer;
  border-bottom: 1px solid #333;
}
.category-header:hover { background: #252525; }
.category-name { flex: 1; font-weight: 500; }
.expand-icon { width: 16px; text-align: center; color: #808080; }

.category-jobs {
  padding: 0 0 8px 28px;
  background: #181818;
}
.category-jobs .data-table.compact { font-size: 0.9em; }
.category-jobs .data-table.compact td,
.category-jobs .data-table.compact th { padding: 4px 8px; }

.add-job-row {
  padding: 4px 0;
}
.add-job-row select {
  background: #2a2a2a;
  color: #e0e0e0;
  border: 1px solid #444;
  padding: 4px 8px;
  border-radius: 4px;
  font-size: 0.85em;
}

.new-category-form {
  display: flex;
  gap: 8px;
  padding: 8px 0;
  align-items: center;
}
.new-category-form input {
  flex: 1;
  max-width: 300px;
}

.category-checkboxes {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.checkbox-label {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 0.9em;
}

.muted-text { color: #808080; }
.count-badge.small { font-size: 0.8em; }
```

**Step 11: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add category view toggle, tag pills, and category management UI"
```

---

### Task 7: Integration Test — Start API, Verify End-to-End

**Files:** None (manual verification)

**Step 1: Start the local environment**

Run: `/setup-local-env restart`

This will apply migration 039 and start the API on port 5000.

**Step 2: Verify migration applied**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT * FROM __MigrationHistory WHERE MigrationName LIKE '%039%'" -W
```
Expected: One row for migration 039

**Step 3: Test category API**

```bash
# Create a category
curl -s -X POST http://localhost:5000/api/categories -H "Content-Type: application/json" -d '{"name":"Electronics"}' | python -m json.tool

# List categories
curl -s http://localhost:5000/api/categories | python -m json.tool

# Assign a job to the category (use actual job and category IDs from above)
curl -s -X POST http://localhost:5000/api/jobs/1/categories -H "Content-Type: application/json" -d '[1]' | python -m json.tool

# Verify jobs response includes categories
curl -s http://localhost:5000/api/jobs | python -m json.tool | head -30
```

**Step 4: Open Electron UI and verify**

- Click "Job Overview"
- Verify Jobs/Categories toggle appears
- Switch to Categories view, create a category
- Expand category, add a job
- Switch back to Jobs view, verify tag pills appear
- Edit a job, verify category checkboxes work

**Step 5: Commit any fixes if needed**
