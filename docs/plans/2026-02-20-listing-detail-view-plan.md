# Listing Detail View Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a full-page listing detail view with comparable inspection and dismiss+recalculate functionality.

**Architecture:** New `GET /api/listings/{id}` and `DELETE /api/listings/{id}/comparables/{relationshipId}` endpoints in `ListingEndpoints.cs`. New `listing-detail` view in the Electron desktop app. EF Core queries with eager-loaded relationships. `ListingPrediction` is a SQL view that auto-updates when relationships change.

**Tech Stack:** ASP.NET Core 8 minimal API, EF Core, Vue 3, Electron

**Design doc:** `docs/plans/2026-02-20-listing-detail-view-design.md`

---

### Task 1: Add `GET /api/listings/{id}` endpoint

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs`
- Test: `AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs`

**Step 1: Write failing tests**

Create `AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs`:

```csharp
using System.Reflection;
using AIOMarketMaker.Api.Endpoints;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Tests.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class ListingEndpoints_UnitTests
{
    private EtlDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = InMemoryDbContextFactory.Create();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    private async Task<IResult> CallGetListingDetail(int id)
    {
        var method = typeof(ListingEndpoints).GetMethod(
            "GetListingDetail",
            BindingFlags.NonPublic | BindingFlags.Static);

        var resultTask = (Task<IResult>)method!.Invoke(null, new object[] { _db, id })!;
        return await resultTask;
    }

    [Test]
    public async Task Should_return_404_when_listing_not_found()
    {
        var result = await CallGetListingDetail(999);
        Assert.That(result, Is.TypeOf<NotFound>());
    }

    [Test]
    public async Task Should_return_listing_with_empty_comparables()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var listing = new Listing
        {
            ListingId = "111", Title = "PS5 Console", Price = 350m,
            Currency = "GBP", Condition = "New", ListingStatus = "Active",
            ScrapeJobId = job.Id
        };
        _db.Listings.Add(listing);
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(listing.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Listing.ListingId, Is.EqualTo("111"));
            Assert.That(response.Listing.Title, Is.EqualTo("PS5 Console"));
            Assert.That(response.Listing.SearchTerm, Is.EqualTo("PS5"));
            Assert.That(response.Comparables, Is.Empty);
        });
    }

    [Test]
    public async Task Should_return_comparables_where_listing_is_A_side()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5 Active", Price = 350m,
            ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            ListingStatus = "Sold", ScrapeJobId = job.Id,
            Description = "Good condition PS5"
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold.Id,
            IsComparable = true, SimilarityScore = 0.92,
            Explanation = "Same console"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.Multiple(() =>
        {
            Assert.That(response.Comparables.Count(), Is.EqualTo(1));
            var comp = response.Comparables.First();
            Assert.That(comp.ListingId, Is.EqualTo("222"));
            Assert.That(comp.Title, Is.EqualTo("PS5 Sold"));
            Assert.That(comp.Description, Is.EqualTo("Good condition PS5"));
            Assert.That(comp.SimilarityScore, Is.EqualTo(0.92).Within(0.001));
        });
    }

    [Test]
    public async Task Should_return_comparables_where_listing_is_B_side()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", Title = "PS5 Active", Price = 350m,
            ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", Title = "PS5 Sold", Price = 380m,
            ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        // Relationship where active listing is on B side
        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = sold.Id, ListingIdB = active.Id,
            IsComparable = true, SimilarityScore = 0.88,
            Explanation = "Same product"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;
        var response = ok.Value!;

        Assert.That(response.Comparables.Count(), Is.EqualTo(1));
        Assert.That(response.Comparables.First().ListingId, Is.EqualTo("222"));
    }

    [Test]
    public async Task Should_exclude_non_comparable_relationships()
    {
        var job = new ScrapeJob { SearchTerm = "PS5" };
        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync();

        var active = new Listing
        {
            ListingId = "111", ListingStatus = "Active", ScrapeJobId = job.Id
        };
        var sold = new Listing
        {
            ListingId = "222", ListingStatus = "Sold", ScrapeJobId = job.Id
        };
        _db.Listings.AddRange(active, sold);
        await _db.SaveChangesAsync();

        _db.ListingRelationships.Add(new ListingRelationship
        {
            ListingIdA = active.Id, ListingIdB = sold.Id,
            IsComparable = false, SimilarityScore = 0.3,
            Explanation = "Different product"
        });
        await _db.SaveChangesAsync();

        var result = await CallGetListingDetail(active.Id);
        var ok = (Ok<ListingDetailResponse>)result;

        Assert.That(ok.Value!.Comparables, Is.Empty);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingEndpoints_UnitTests" -v n`
Expected: Build fails — `ListingDetailResponse` and `GetListingDetail` don't exist yet.

**Step 3: Add response records and implement endpoint**

In `AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs`, add these records after the existing records at the top of the file (before `public static class ListingEndpoints`):

```csharp
public record ListingDetail(
    int Id, string ListingId, string? Title, string? Description,
    decimal? Price, string? Currency, decimal? ShippingCost,
    string? Condition, string? Url, string? Images,
    string? ListingStatus, string? SearchTerm,
    DateTime CreatedUtc,
    decimal? AverageSoldPrice, int SimilarSoldCount,
    int? EstimatedDaysToSell, decimal? PotentialProfit);

public record ComparableListing(
    int RelationshipId, string ListingId, string? Title,
    string? Description, decimal? Price, string? Condition,
    string? Url, string? Images,
    DateTime? SoldDateUtc, double SimilarityScore, string Explanation);

public record ListingDetailResponse(
    ListingDetail Listing, IEnumerable<ComparableListing> Comparables);
```

In `MapListingEndpoints`, add these two route registrations:

```csharp
app.MapGet("/api/listings/{id:int}", GetListingDetail);
app.MapDelete("/api/listings/{id:int}/comparables/{relationshipId:int}", DismissComparable);
```

Add the `GetListingDetail` method:

```csharp
private static async Task<IResult> GetListingDetail(EtlDbContext db, int id)
{
    var listing = await db.Listings
        .Include(l => l.ScrapeJob)
        .FirstOrDefaultAsync(l => l.Id == id);

    if (listing == null)
    {
        return Results.NotFound();
    }

    // Get prediction data (may not exist if no comps)
    var prediction = await db.ListingPredictions
        .FirstOrDefaultAsync(p => p.ListingId == id);

    // Get all comparable relationships (bidirectional)
    var relationships = await db.ListingRelationships
        .Include(r => r.ListingA)
        .Include(r => r.ListingB)
        .Where(r => r.IsComparable && (r.ListingIdA == id || r.ListingIdB == id))
        .ToListAsync();

    var comparables = relationships.Select(r =>
    {
        var comp = r.ListingIdA == id ? r.ListingB : r.ListingA;
        return new ComparableListing(
            r.Id, comp.ListingId, comp.Title, comp.Description,
            comp.Price, comp.Condition, comp.Url, comp.Images,
            comp.EndDateUtc, r.SimilarityScore, r.Explanation);
    });

    var detail = new ListingDetail(
        listing.Id, listing.ListingId, listing.Title, listing.Description,
        listing.Price, listing.Currency, listing.ShippingCost,
        listing.Condition, listing.Url, listing.Images,
        listing.ListingStatus, listing.ScrapeJob?.SearchTerm,
        listing.CreatedUtc,
        prediction?.AverageSoldPrice, prediction?.SimilarSoldCount ?? 0,
        prediction?.EstimatedDaysToSell, prediction?.PotentialProfit);

    return Results.Ok(new ListingDetailResponse(detail, comparables));
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingEndpoints_UnitTests" -v n`
Expected: All 5 tests pass.

Note: The `ListingPrediction` query will return null in InMemory tests because it's backed by a SQL view. That's fine — the tests verify relationship logic. The prediction integration is covered by E2E tests.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs
git commit -m "feat: add GET /api/listings/{id} detail endpoint with comparables"
```

---

### Task 2: Add `DELETE /api/listings/{id}/comparables/{relationshipId}` endpoint

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs`
- Modify: `AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs`

**Step 1: Write failing tests**

Add these tests to `ListingEndpoints_UnitTests.cs`:

```csharp
private async Task<IResult> CallDismissComparable(int listingId, int relationshipId)
{
    var method = typeof(ListingEndpoints).GetMethod(
        "DismissComparable",
        BindingFlags.NonPublic | BindingFlags.Static);

    var resultTask = (Task<IResult>)method!.Invoke(null, new object[] { _db, listingId, relationshipId })!;
    return await resultTask;
}

[Test]
public async Task Should_return_404_when_relationship_not_found()
{
    var job = new ScrapeJob { SearchTerm = "PS5" };
    _db.ScrapeJobs.Add(job);
    await _db.SaveChangesAsync();

    var listing = new Listing
    {
        ListingId = "111", ListingStatus = "Active", ScrapeJobId = job.Id
    };
    _db.Listings.Add(listing);
    await _db.SaveChangesAsync();

    var result = await CallDismissComparable(listing.Id, 999);
    Assert.That(result, Is.TypeOf<NotFound>());
}

[Test]
public async Task Should_delete_relationship_and_return_updated_detail()
{
    var job = new ScrapeJob { SearchTerm = "PS5" };
    _db.ScrapeJobs.Add(job);
    await _db.SaveChangesAsync();

    var active = new Listing
    {
        ListingId = "111", Title = "PS5", Price = 350m,
        ListingStatus = "Active", ScrapeJobId = job.Id
    };
    var sold1 = new Listing
    {
        ListingId = "222", Title = "PS5 Sold 1", Price = 380m,
        ListingStatus = "Sold", ScrapeJobId = job.Id
    };
    var sold2 = new Listing
    {
        ListingId = "333", Title = "PS5 Sold 2", Price = 400m,
        ListingStatus = "Sold", ScrapeJobId = job.Id
    };
    _db.Listings.AddRange(active, sold1, sold2);
    await _db.SaveChangesAsync();

    var rel1 = new ListingRelationship
    {
        ListingIdA = active.Id, ListingIdB = sold1.Id,
        IsComparable = true, SimilarityScore = 0.9, Explanation = "Same"
    };
    var rel2 = new ListingRelationship
    {
        ListingIdA = active.Id, ListingIdB = sold2.Id,
        IsComparable = true, SimilarityScore = 0.85, Explanation = "Same"
    };
    _db.ListingRelationships.AddRange(rel1, rel2);
    await _db.SaveChangesAsync();

    // Dismiss rel1
    var result = await CallDismissComparable(active.Id, rel1.Id);
    var ok = (Ok<ListingDetailResponse>)result;
    var response = ok.Value!;

    Assert.Multiple(() =>
    {
        // Should only have 1 comp remaining
        Assert.That(response.Comparables.Count(), Is.EqualTo(1));
        Assert.That(response.Comparables.First().ListingId, Is.EqualTo("333"));

        // Relationship should be deleted from DB
        Assert.That(_db.ListingRelationships.Count(), Is.EqualTo(1));
    });
}

[Test]
public async Task Should_reject_dismiss_when_relationship_does_not_belong_to_listing()
{
    var job = new ScrapeJob { SearchTerm = "PS5" };
    _db.ScrapeJobs.Add(job);
    await _db.SaveChangesAsync();

    var listing1 = new Listing
    {
        ListingId = "111", ListingStatus = "Active", ScrapeJobId = job.Id
    };
    var listing2 = new Listing
    {
        ListingId = "222", ListingStatus = "Active", ScrapeJobId = job.Id
    };
    var sold = new Listing
    {
        ListingId = "333", ListingStatus = "Sold", ScrapeJobId = job.Id
    };
    _db.Listings.AddRange(listing1, listing2, sold);
    await _db.SaveChangesAsync();

    var rel = new ListingRelationship
    {
        ListingIdA = listing2.Id, ListingIdB = sold.Id,
        IsComparable = true, SimilarityScore = 0.9, Explanation = "Same"
    };
    _db.ListingRelationships.Add(rel);
    await _db.SaveChangesAsync();

    // Try to dismiss rel via listing1 (doesn't own it)
    var result = await CallDismissComparable(listing1.Id, rel.Id);
    Assert.That(result, Is.TypeOf<NotFound>());

    // Relationship should still exist
    Assert.That(_db.ListingRelationships.Count(), Is.EqualTo(1));
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingEndpoints_UnitTests" -v n`
Expected: Fails — `DismissComparable` method doesn't exist yet.

**Step 3: Implement the dismiss endpoint**

Add to `ListingEndpoints.cs`:

```csharp
private static async Task<IResult> DismissComparable(
    EtlDbContext db, int id, int relationshipId)
{
    var relationship = await db.ListingRelationships
        .FirstOrDefaultAsync(r =>
            r.Id == relationshipId &&
            (r.ListingIdA == id || r.ListingIdB == id));

    if (relationship == null)
    {
        return Results.NotFound();
    }

    db.ListingRelationships.Remove(relationship);
    await db.SaveChangesAsync();

    // Return updated detail (reuse GetListingDetail logic)
    return await GetListingDetail(db, id);
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ListingEndpoints_UnitTests" -v n`
Expected: All 8 tests pass.

**Step 5: Build the full solution**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds with no errors.

**Step 6: Commit**

```bash
git add AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs AIOMarketMaker.Tests.Unit/Endpoints/ListingEndpoints_UnitTests.cs
git commit -m "feat: add DELETE /api/listings/{id}/comparables/{relationshipId} dismiss endpoint"
```

---

### Task 3: Add listing detail view to Desktop UI

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Add state variables to app.js**

In `app.js`, add these to the `data()` return object (after the `opportunities` related state around line 17):

```javascript
// Listing detail view
selectedListingId: null,
listingDetail: null,
listingDetailLoading: false,
```

**Step 2: Add API methods to app.js**

Add these methods in the `methods` section:

```javascript
async loadListingDetail(id) {
    this.listingDetailLoading = true;
    try {
        const data = await this.apiCall(`/listings/${id}`);
        this.listingDetail = this.toCamelCase(data);
    } catch (err) {
        this.showToast(`Failed to load listing: ${err.message}`, 'error');
    } finally {
        this.listingDetailLoading = false;
    }
},

openListingDetail(listing) {
    this.selectedListingId = listing.id;
    this.currentView = 'listing-detail';
    this.loadListingDetail(listing.id);
},

backToOpportunities() {
    this.currentView = 'opportunities';
    this.listingDetail = null;
    this.selectedListingId = null;
},

async dismissComparable(relationshipId) {
    try {
        const data = await this.apiCall(
            `/listings/${this.selectedListingId}/comparables/${relationshipId}`,
            { method: 'DELETE' }
        );
        this.listingDetail = this.toCamelCase(data);
        const count = this.listingDetail.comparables?.length || 0;
        this.showToast(`Comparable dismissed. ${count} remaining.`, 'success');
    } catch (err) {
        this.showToast(`Failed to dismiss: ${err.message}`, 'error');
    }
},

parseImages(imagesJson) {
    if (!imagesJson) { return []; }
    try {
        return JSON.parse(imagesJson);
    } catch {
        return [];
    }
},

firstImage(imagesJson) {
    const images = this.parseImages(imagesJson);
    return images.length > 0 ? images[0] : null;
},
```

**Step 3: Make opportunity rows clickable**

In `index.html`, find the opportunities table row (line ~161):

```html
<tr v-for="listing in opportunities" :key="listing.id">
```

Replace with:

```html
<tr v-for="listing in opportunities" :key="listing.id" @click="openListingDetail(listing)" style="cursor: pointer;">
```

Change the "View" button in the Actions column (line ~186) to stop propagation so clicking it doesn't also open the detail view:

```html
<a v-if="listing.url" :href="listing.url" target="_blank" class="btn small" @click.stop>eBay</a>
```

**Step 4: Add listing detail view HTML**

In `index.html`, after the opportunities view closing `</div>` (line ~205) and before the Index view `<div v-if="currentView === 'index'"`, add:

```html
<!-- Listing Detail View -->
<div v-if="currentView === 'listing-detail'" class="view">
    <div v-if="listingDetailLoading" class="loading">Loading...</div>
    <div v-else-if="listingDetail">
        <!-- Header -->
        <div class="detail-header">
            <button class="btn" @click="backToOpportunities">&larr; Back</button>
            <h1 class="detail-title">{{ listingDetail.listing.title || 'Untitled' }}</h1>
            <a v-if="listingDetail.listing.url" :href="listingDetail.listing.url" target="_blank" class="btn">View on eBay</a>
        </div>

        <!-- Anchor Listing Card -->
        <div class="detail-anchor">
            <div class="detail-anchor-image">
                <img v-if="firstImage(listingDetail.listing.images)" :src="firstImage(listingDetail.listing.images)" alt="Listing image">
                <div v-else class="image-placeholder">No Image</div>
            </div>
            <div class="detail-anchor-info">
                <div class="detail-meta">
                    <div class="detail-meta-item">
                        <span class="meta-label">Price</span>
                        <span class="meta-value">{{ formatPrice(listingDetail.listing.price, listingDetail.listing.currency) }}
                            <span v-if="listingDetail.listing.shippingCost"> + {{ formatPrice(listingDetail.listing.shippingCost, listingDetail.listing.currency) }} shipping</span>
                        </span>
                    </div>
                    <div class="detail-meta-item">
                        <span class="meta-label">Condition</span>
                        <span class="meta-value">{{ listingDetail.listing.condition || '-' }}</span>
                    </div>
                    <div class="detail-meta-item">
                        <span class="meta-label">Status</span>
                        <span class="meta-value">{{ listingDetail.listing.listingStatus || '-' }}</span>
                    </div>
                    <div class="detail-meta-item">
                        <span class="meta-label">Job</span>
                        <span class="meta-value">{{ listingDetail.listing.searchTerm || '-' }}</span>
                    </div>
                </div>
                <div class="detail-metrics">
                    <div class="metric-card">
                        <span class="metric-value">{{ formatPrice(listingDetail.listing.averageSoldPrice, listingDetail.listing.currency) || '-' }}</span>
                        <span class="metric-label">Avg Sold</span>
                    </div>
                    <div class="metric-card">
                        <span class="metric-value">{{ listingDetail.listing.similarSoldCount || 0 }}</span>
                        <span class="metric-label">Comps</span>
                    </div>
                    <div class="metric-card">
                        <span class="metric-value" :style="{ color: (listingDetail.listing.potentialProfit || 0) >= 0 ? '#22c55e' : '#ef4444' }">
                            {{ listingDetail.listing.potentialProfit != null ? (listingDetail.listing.potentialProfit >= 0 ? '+' : '') + formatPrice(listingDetail.listing.potentialProfit, listingDetail.listing.currency) : '-' }}
                        </span>
                        <span class="metric-label">Profit</span>
                    </div>
                    <div class="metric-card">
                        <span class="metric-value">{{ listingDetail.listing.estimatedDaysToSell != null ? listingDetail.listing.estimatedDaysToSell + 'd' : '-' }}</span>
                        <span class="metric-label">Days to Sell</span>
                    </div>
                </div>
                <div v-if="listingDetail.listing.description" class="detail-description">
                    <span class="meta-label">Description</span>
                    <div class="description-text">{{ listingDetail.listing.description }}</div>
                </div>
            </div>
        </div>

        <!-- Comparables Section -->
        <div class="detail-comps-header">
            <h2>Comparables ({{ listingDetail.comparables.length }})</h2>
        </div>
        <div v-if="listingDetail.comparables.length === 0" class="empty">No comparables found</div>
        <div v-else class="comp-grid">
            <div v-for="comp in listingDetail.comparables" :key="comp.relationshipId" class="comp-card">
                <div class="comp-image">
                    <img v-if="firstImage(comp.images)" :src="firstImage(comp.images)" alt="Comp image">
                    <div v-else class="image-placeholder small">No Image</div>
                </div>
                <div class="comp-info">
                    <div class="comp-title">{{ comp.title || 'Untitled' }}</div>
                    <div v-if="comp.description" class="comp-description">{{ truncate(comp.description, 150) }}</div>
                    <div class="comp-meta">
                        <span class="comp-price">{{ formatPrice(comp.price, listingDetail.listing.currency) }}</span>
                        <span v-if="comp.condition" class="comp-condition">{{ comp.condition }}</span>
                        <span class="comp-score">Score: {{ comp.similarityScore.toFixed(2) }}</span>
                    </div>
                </div>
                <div class="comp-actions">
                    <a v-if="comp.url" :href="comp.url" target="_blank" class="btn small">eBay</a>
                    <button class="btn small danger" @click="dismissComparable(comp.relationshipId)">Dismiss</button>
                </div>
            </div>
        </div>
    </div>
</div>
```

**Step 5: Add CSS styles**

Add to the end of `styles.css`:

```css
/* Listing Detail View */
.detail-header {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-bottom: 20px;
}
.detail-header h1 {
    flex: 1;
    font-size: 1.3rem;
    margin: 0;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.detail-title {
    min-width: 0;
}

.detail-anchor {
    display: flex;
    gap: 20px;
    background: var(--card-bg, #1e1e2e);
    border: 1px solid var(--border-color, #333);
    border-radius: 8px;
    padding: 20px;
    margin-bottom: 24px;
}
.detail-anchor-image {
    flex-shrink: 0;
    width: 200px;
    height: 200px;
}
.detail-anchor-image img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    border-radius: 4px;
}
.detail-anchor-info {
    flex: 1;
    min-width: 0;
}

.detail-meta {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
    gap: 8px;
    margin-bottom: 16px;
}
.detail-meta-item {
    display: flex;
    flex-direction: column;
    gap: 2px;
}
.meta-label {
    font-size: 0.75rem;
    color: #888;
    text-transform: uppercase;
}
.meta-value {
    font-size: 0.95rem;
}

.detail-metrics {
    display: flex;
    gap: 16px;
    margin-bottom: 16px;
}
.metric-card {
    display: flex;
    flex-direction: column;
    align-items: center;
    padding: 8px 16px;
    background: rgba(255,255,255,0.05);
    border-radius: 6px;
}
.metric-value {
    font-size: 1.1rem;
    font-weight: 600;
}
.metric-label {
    font-size: 0.7rem;
    color: #888;
    text-transform: uppercase;
}

.detail-description {
    margin-top: 8px;
}
.description-text {
    max-height: 200px;
    overflow-y: auto;
    font-size: 0.85rem;
    color: #ccc;
    line-height: 1.5;
    white-space: pre-wrap;
    margin-top: 4px;
}

.detail-comps-header {
    margin-bottom: 12px;
}
.detail-comps-header h2 {
    font-size: 1.1rem;
    margin: 0;
}

.image-placeholder {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 100%;
    height: 100%;
    background: rgba(255,255,255,0.05);
    border-radius: 4px;
    color: #666;
    font-size: 0.8rem;
}
.image-placeholder.small {
    height: 80px;
}

/* Comp Cards Grid */
.comp-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
    gap: 12px;
}
.comp-card {
    background: var(--card-bg, #1e1e2e);
    border: 1px solid var(--border-color, #333);
    border-radius: 8px;
    padding: 12px;
    display: flex;
    flex-direction: column;
    gap: 8px;
}
.comp-image {
    width: 100%;
    height: 120px;
}
.comp-image img {
    width: 100%;
    height: 100%;
    object-fit: contain;
    border-radius: 4px;
}
.comp-info {
    flex: 1;
}
.comp-title {
    font-weight: 600;
    font-size: 0.9rem;
    margin-bottom: 4px;
}
.comp-description {
    font-size: 0.8rem;
    color: #aaa;
    line-height: 1.4;
    margin-bottom: 8px;
    display: -webkit-box;
    -webkit-line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
}
.comp-meta {
    display: flex;
    gap: 12px;
    font-size: 0.8rem;
    color: #ccc;
}
.comp-price {
    font-weight: 600;
    color: #22c55e;
}
.comp-condition {
    color: #888;
}
.comp-score {
    color: #60a5fa;
}
.comp-actions {
    display: flex;
    gap: 8px;
    justify-content: flex-end;
}

.btn.danger {
    background: #dc2626;
    color: white;
}
.btn.danger:hover {
    background: #b91c1c;
}
```

**Step 6: Verify manually**

1. Make sure the local environment is running (`/setup-local-env start`)
2. Open the Desktop app
3. Go to Opportunities tab
4. Click on any row — should navigate to the detail view
5. Verify anchor listing data displays correctly
6. Verify comparables show with images/titles/descriptions
7. Click "Dismiss" on a comp — should remove it and update metrics
8. Click "Back" — should return to opportunities with same page/filters

**Step 7: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html AIOMarketMaker.Desktop/electron/src/app.js AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add listing detail view with comp cards and dismiss functionality"
```

---

### Task 4: Run all tests and verify

**Step 1: Run all unit tests**

Run: `dotnet test AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj -v n`
Expected: All tests pass (existing + 8 new listing detail tests).

**Step 2: Run full solution build**

Run: `dotnet build AIOMarketMaker.sln`
Expected: Build succeeds.

**Step 3: Manual E2E verification**

With the local environment running:
1. Open Desktop app
2. Navigate to Opportunities
3. Click a listing row — detail view loads
4. Verify image, title, description, price, metrics all display
5. Scroll down to comparables
6. Dismiss one comp — metrics recalculate, toast shows
7. Click "Back" — returns to opportunities
8. Click "View on eBay" — opens eBay in browser
