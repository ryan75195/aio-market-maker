using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace AIOMarketMaker.Controllers
{
    public record StatusCount(string? Status, int Count);

    public record CreateJobRequest(
        string SearchTerm,
        string BuyingFormat,
        string Condition,
        string SearchType,
        int FrequencyMinutes = 60,
        int? LookbackDays = null,
        int? ItemLimit = null,
        bool IsEnabled = true
    );

    public class DashboardController
    {
        private readonly EtlDbContext _dbContext;
        private readonly ILogger<DashboardController> _logger;
        private static readonly string _wwwrootPath;

        static DashboardController()
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? ".";
            _wwwrootPath = Path.Combine(assemblyDir, "wwwroot");
        }

        public DashboardController(
            EtlDbContext dbContext,
            ILogger<DashboardController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        #region Jobs API

        [Function("GetJobs")]
        public async Task<HttpResponseData> GetJobs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs")]
            HttpRequestData req)
        {
            var jobs = await _dbContext.ScrapeJobs
                .Select(j => new
                {
                    j.Id,
                    j.SearchTerm,
                    j.BuyingFormat,
                    j.Condition,
                    j.SearchType,
                    j.FrequencyMinutes,
                    j.LookbackDays,
                    j.ItemLimit,
                    j.IsEnabled,
                    j.LastRunUtc,
                    j.CreatedUtc
                })
                .ToListAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(jobs);
            return response;
        }

        [Function("CreateJob")]
        public async Task<HttpResponseData> CreateJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs")]
            HttpRequestData req)
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrEmpty(body))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Request body is required" });
                return badRequest;
            }

            CreateJobRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<CreateJobRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Invalid JSON" });
                return badRequest;
            }

            if (request == null || string.IsNullOrEmpty(request.SearchTerm))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "SearchTerm is required" });
                return badRequest;
            }

            var job = new ScrapeJob
            {
                SearchTerm = request.SearchTerm,
                BuyingFormat = request.BuyingFormat ?? "BUY_NOW",
                Condition = request.Condition ?? "USED",
                SearchType = request.SearchType ?? "SOLD",
                FrequencyMinutes = request.FrequencyMinutes,
                LookbackDays = request.LookbackDays,
                ItemLimit = request.ItemLimit,
                IsEnabled = request.IsEnabled,
                CreatedUtc = DateTime.UtcNow
            };

            _dbContext.ScrapeJobs.Add(job);
            await _dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new
            {
                job.Id,
                job.SearchTerm,
                job.BuyingFormat,
                job.Condition,
                job.SearchType,
                job.FrequencyMinutes,
                job.LookbackDays,
                job.ItemLimit,
                job.IsEnabled,
                job.CreatedUtc
            });
            return response;
        }

        [Function("DeleteJob")]
        public async Task<HttpResponseData> DeleteJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "jobs/{id:int}")]
            HttpRequestData req,
            int id)
        {
            var job = await _dbContext.ScrapeJobs.FindAsync(id);
            if (job == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            var listingCount = await _dbContext.Listings.Where(l => l.ScrapeJobId == id).CountAsync();
            // Products are cascade deleted via FK to Listings
            _dbContext.Listings.RemoveRange(_dbContext.Listings.Where(l => l.ScrapeJobId == id));
            _dbContext.ScrapeJobs.Remove(job);
            await _dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { message = $"Job {id} deleted along with {listingCount} listings" });
            return response;
        }

        [Function("ToggleJob")]
        public async Task<HttpResponseData> ToggleJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs/{id:int}/toggle")]
            HttpRequestData req,
            int id)
        {
            var job = await _dbContext.ScrapeJobs.FindAsync(id);
            if (job == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            job.IsEnabled = !job.IsEnabled;
            await _dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { job.Id, job.IsEnabled });
            return response;
        }

        [Function("UpdateJob")]
        public async Task<HttpResponseData> UpdateJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "jobs/{id:int}")]
            HttpRequestData req,
            int id)
        {
            var job = await _dbContext.ScrapeJobs.FindAsync(id);
            if (job == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrEmpty(body))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Request body is required" });
                return badRequest;
            }

            CreateJobRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<CreateJobRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Invalid JSON" });
                return badRequest;
            }

            if (request == null || string.IsNullOrEmpty(request.SearchTerm))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "SearchTerm is required" });
                return badRequest;
            }

            job.SearchTerm = request.SearchTerm;
            job.BuyingFormat = request.BuyingFormat ?? job.BuyingFormat;
            job.Condition = request.Condition ?? job.Condition;
            job.SearchType = request.SearchType ?? job.SearchType;
            job.FrequencyMinutes = request.FrequencyMinutes;
            job.LookbackDays = request.LookbackDays;
            job.ItemLimit = request.ItemLimit;
            job.IsEnabled = request.IsEnabled;

            await _dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                job.Id,
                job.SearchTerm,
                job.BuyingFormat,
                job.Condition,
                job.SearchType,
                job.FrequencyMinutes,
                job.LookbackDays,
                job.ItemLimit,
                job.IsEnabled,
                job.LastRunUtc,
                job.CreatedUtc
            });
            return response;
        }

        #endregion

        #region Listings API

        [Function("GetListings")]
        public async Task<HttpResponseData> GetListings(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "listings")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var page = int.TryParse(query["page"], out var p) ? p : 1;
            var pageSize = int.TryParse(query["pageSize"], out var ps) ? ps : 50;
            var status = query["status"];
            var jobId = int.TryParse(query["jobId"], out var jid) ? jid : (int?)null;
            var search = query["search"];

            var listingsQuery = _dbContext.Listings.AsQueryable();

            if (!string.IsNullOrEmpty(status))
                listingsQuery = listingsQuery.Where(x => x.ListingStatus == status);

            if (jobId.HasValue)
                listingsQuery = listingsQuery.Where(x => x.ScrapeJobId == jobId.Value);

            if (!string.IsNullOrEmpty(search))
                listingsQuery = listingsQuery.Where(x => x.Title != null && x.Title.Contains(search));

            var total = await listingsQuery.CountAsync();
            var listings = await listingsQuery
                .OrderByDescending(x => x.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    id = x.Id,
                    listingId = x.ListingId,
                    title = x.Title,
                    price = x.Price,
                    currency = x.Currency,
                    listingStatus = x.ListingStatus,
                    condition = x.Condition,
                    url = x.Url,
                    endDateUtc = x.EndDateUtc,
                    createdUtc = x.CreatedUtc,
                    scrapeJobId = x.ScrapeJobId
                })
                .ToListAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { total, page, pageSize, listings });
            return response;
        }

        /// <summary>
        /// Get full listing details including status history and normalized product data
        /// GET /api/listings/{id}/details
        /// </summary>
        [Function("GetListingDetails")]
        public async Task<HttpResponseData> GetListingDetails(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "listings/{id:int}/details")]
            HttpRequestData req,
            int id)
        {
            var listing = await _dbContext.Listings
                .Include(l => l.StatusHistory)
                .Include(l => l.ScrapeJob)
                .Include(l => l.Product)
                .FirstOrDefaultAsync(l => l.Id == id);

            if (listing == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Listing {id} not found" });
                return notFound;
            }

            var history = listing.StatusHistory
                .OrderByDescending(h => h.RecordedUtc)
                .Select(h => new
                {
                    id = h.Id,
                    listingStatus = h.ListingStatus,
                    price = h.Price,
                    soldDateUtc = h.SoldDateUtc,
                    recordedUtc = h.RecordedUtc,
                    source = h.Source
                })
                .ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                listing = new
                {
                    id = listing.Id,
                    listingId = listing.ListingId,
                    title = listing.Title,
                    price = listing.Price,
                    currency = listing.Currency,
                    shippingCost = listing.ShippingCost,
                    condition = listing.Condition,
                    listingStatus = listing.ListingStatus,
                    purchaseFormat = listing.PurchaseFormat,
                    description = listing.Description,
                    itemSpecifics = listing.ItemSpecifics,
                    images = listing.Images,
                    location = listing.Location,
                    url = listing.Url,
                    endDateUtc = listing.EndDateUtc,
                    createdUtc = listing.CreatedUtc,
                    updatedUtc = listing.UpdatedUtc,
                    job = listing.ScrapeJob != null ? new
                    {
                        id = listing.ScrapeJob.Id,
                        searchTerm = listing.ScrapeJob.SearchTerm
                    } : null
                },
                product = listing.Product != null ? new
                {
                    id = listing.Product.Id,
                    category = listing.Product.Category,
                    categoryConfidence = listing.Product.CategoryConfidence,
                    brand = listing.Product.Brand,
                    model = listing.Product.Model,
                    storageCapacity = listing.Product.StorageCapacity,
                    color = listing.Product.Color,
                    edition = listing.Product.Edition,
                    variantType = listing.Product.VariantType,
                    bundledItems = listing.Product.BundledItems,
                    listedDateUtc = listing.Product.ListedDateUtc,
                    soldDateUtc = listing.Product.SoldDateUtc,
                    resolvedUtc = listing.Product.ResolvedUtc
                } : null,
                history
            });
            return response;
        }

        /// <summary>
        /// Get normalized/classified products with filtering
        /// GET /api/products?category=base_product&brand=Sony&page=1&pageSize=50
        /// </summary>
        [Function("GetProducts")]
        public async Task<HttpResponseData> GetProducts(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var page = int.TryParse(query["page"], out var p) ? p : 1;
            var pageSize = int.TryParse(query["pageSize"], out var ps) ? ps : 50;
            var category = query["category"];
            var brand = query["brand"];
            var model = query["model"];
            var productName = query["productName"];
            var status = query["status"];
            var search = query["search"];

            var productsQuery = _dbContext.Products.AsQueryable();

            if (!string.IsNullOrEmpty(category))
                productsQuery = productsQuery.Where(p => p.Category == category);

            if (!string.IsNullOrEmpty(brand))
                productsQuery = productsQuery.Where(p => p.Brand == brand);

            if (!string.IsNullOrEmpty(model))
                productsQuery = productsQuery.Where(p => p.Model != null && p.Model.Contains(model));

            if (!string.IsNullOrEmpty(productName))
                productsQuery = productsQuery.Where(p => p.ProductName != null && p.ProductName.Contains(productName));

            if (!string.IsNullOrEmpty(status))
                productsQuery = productsQuery.Where(p => p.ListingStatus == status);

            if (!string.IsNullOrEmpty(search))
                productsQuery = productsQuery.Where(p => p.Title != null && p.Title.Contains(search));

            var total = await productsQuery.CountAsync();
            var products = await productsQuery
                .OrderByDescending(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new
                {
                    // Core identification
                    id = p.Id,
                    ebayListingId = p.EbayListingId,
                    productName = p.ProductName,
                    title = p.Title,
                    url = p.Url,
                    // Pricing
                    price = p.Price,
                    currency = p.Currency,
                    shippingCost = p.ShippingCost,
                    // Classification
                    category = p.Category,
                    categoryConfidence = p.CategoryConfidence,
                    condition = p.Condition,
                    listingStatus = p.ListingStatus,
                    purchaseFormat = p.PurchaseFormat,
                    // Product attributes
                    brand = p.Brand,
                    model = p.Model,
                    storageCapacity = p.StorageCapacity,
                    color = p.Color,
                    edition = p.Edition,
                    variantType = p.VariantType,
                    bundledItems = p.BundledItems,
                    // Location
                    location = p.Location,
                    // Dates
                    listedDateUtc = p.ListedDateUtc,
                    soldDateUtc = p.SoldDateUtc,
                    endDateUtc = p.EndDateUtc,
                    resolvedUtc = p.ResolvedUtc
                })
                .ToListAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { total, page, pageSize, products });
            return response;
        }

        /// <summary>
        /// Get distinct product names with counts, optionally filtered by category
        /// GET /api/products/names?category=base_product
        /// </summary>
        [Function("GetProductNames")]
        public async Task<HttpResponseData> GetProductNames(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/names")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var category = query["category"];

            var productsQuery = _dbContext.Products.AsQueryable();

            if (!string.IsNullOrEmpty(category))
                productsQuery = productsQuery.Where(p => p.Category == category);

            var productNames = await productsQuery
                .Where(p => p.ProductName != null)
                .GroupBy(p => p.ProductName)
                .Select(g => new
                {
                    productName = g.Key,
                    count = g.Count(),
                    soldCount = g.Count(p => p.ListingStatus == "Sold"),
                    activeCount = g.Count(p => p.ListingStatus != "Sold"),
                    avgPrice = g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
                })
                .OrderByDescending(x => x.count)
                .ToListAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(productNames);
            return response;
        }

        #endregion

        #region Metrics API

        [Function("GetMetrics")]
        public async Task<HttpResponseData> GetMetrics(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics")]
            HttpRequestData req)
        {
            // Load denormalized products (no joins needed)
            var products = await _dbContext.Products.ToListAsync();
            var jobs = await _dbContext.ScrapeJobs.ToListAsync();

            // Separate sold and active products
            var soldProducts = products.Where(p => p.ListingStatus == "Sold" && p.Price.HasValue).ToList();
            var activeProducts = products.Where(p => p.ListingStatus != "Sold" && p.Price.HasValue).ToList();

            // Category breakdown
            var categoryBreakdown = products
                .GroupBy(p => p.Category)
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    soldCount = g.Count(p => p.ListingStatus == "Sold"),
                    activeCount = g.Count(p => p.ListingStatus != "Sold"),
                    avgPrice = Math.Round(g.Where(p => p.Price.HasValue).Average(p => (double)p.Price!.Value), 2),
                    avgSoldPrice = g.Any(p => p.ListingStatus == "Sold" && p.Price.HasValue)
                        ? Math.Round(g.Where(p => p.ListingStatus == "Sold" && p.Price.HasValue).Average(p => (double)p.Price!.Value), 2)
                        : 0
                })
                .OrderByDescending(x => x.count)
                .ToList();

            // Brand breakdown
            var brandBreakdown = products
                .Where(p => !string.IsNullOrEmpty(p.Brand))
                .GroupBy(p => p.Brand)
                .Select(g => new
                {
                    brand = g.Key,
                    count = g.Count(),
                    soldCount = g.Count(p => p.ListingStatus == "Sold"),
                    avgPrice = Math.Round(g.Where(p => p.Price.HasValue).Average(p => (double)p.Price!.Value), 2),
                    models = g.Where(p => !string.IsNullOrEmpty(p.Model))
                        .GroupBy(p => p.Model)
                        .Select(m => new { model = m.Key, count = m.Count() })
                        .OrderByDescending(m => m.count)
                        .Take(5)
                        .ToList()
                })
                .OrderByDescending(x => x.count)
                .Take(10)
                .ToList();

            // Product name breakdown
            var productNameBreakdown = products
                .Where(p => !string.IsNullOrEmpty(p.ProductName))
                .GroupBy(p => p.ProductName)
                .Select(g => new
                {
                    productName = g.Key,
                    count = g.Count(),
                    soldCount = g.Count(p => p.ListingStatus == "Sold"),
                    activeCount = g.Count(p => p.ListingStatus != "Sold"),
                    avgPrice = Math.Round(g.Where(p => p.Price.HasValue).Average(p => (double)p.Price!.Value), 2),
                    avgSoldPrice = g.Any(p => p.ListingStatus == "Sold" && p.Price.HasValue)
                        ? Math.Round(g.Where(p => p.ListingStatus == "Sold" && p.Price.HasValue).Average(p => (double)p.Price!.Value), 2)
                        : 0
                })
                .OrderByDescending(x => x.count)
                .Take(20)
                .ToList();

            // Calculate arbitrage metrics per search term/job (with category filtering)
            var arbitrageByJob = jobs.Select(j =>
            {
                // Get products for this job via Listing FK
                var jobProducts = products.Where(p =>
                {
                    var listing = _dbContext.Listings.Find(p.ListingId);
                    return listing?.ScrapeJobId == j.Id;
                }).ToList();

                var jobSold = jobProducts.Where(p => p.ListingStatus == "Sold" && p.Price.HasValue).ToList();
                var jobActive = jobProducts.Where(p => p.ListingStatus != "Sold" && p.Price.HasValue).ToList();

                // Filter to base_product category only for more accurate pricing
                var baseProductSold = jobSold.Where(p => p.Category == "base_product").ToList();
                var baseProductActive = jobActive.Where(p => p.Category == "base_product").ToList();

                var avgSoldPrice = baseProductSold.Any() ? baseProductSold.Average(p => (double)p.Price!.Value) : 0;
                var minSoldPrice = baseProductSold.Any() ? (double)baseProductSold.Min(p => p.Price!.Value) : 0;
                var maxSoldPrice = baseProductSold.Any() ? (double)baseProductSold.Max(p => p.Price!.Value) : 0;
                var medianSoldPrice = baseProductSold.Any() ? GetMedian(baseProductSold.Select(p => (double)p.Price!.Value).ToList()) : 0;

                var avgActivePrice = baseProductActive.Any() ? baseProductActive.Average(p => (double)p.Price!.Value) : 0;
                var minActivePrice = baseProductActive.Any() ? (double)baseProductActive.Min(p => p.Price!.Value) : 0;

                // Deals: active base products priced below median sold price
                var dealsCount = baseProductActive.Count(p => (double)p.Price!.Value < medianSoldPrice * 0.8);

                // Price spread: difference between avg sold and min active (profit potential)
                var priceSpread = avgSoldPrice - minActivePrice;
                var spreadPercent = minActivePrice > 0 ? (priceSpread / minActivePrice) * 100 : 0;

                return new
                {
                    jobId = j.Id,
                    searchTerm = j.SearchTerm,
                    totalCount = jobSold.Count + jobActive.Count,
                    baseProductCount = baseProductSold.Count + baseProductActive.Count,
                    soldCount = baseProductSold.Count,
                    activeCount = baseProductActive.Count,
                    avgSoldPrice = Math.Round(avgSoldPrice, 2),
                    medianSoldPrice = Math.Round(medianSoldPrice, 2),
                    minSoldPrice = Math.Round(minSoldPrice, 2),
                    maxSoldPrice = Math.Round(maxSoldPrice, 2),
                    avgActivePrice = Math.Round(avgActivePrice, 2),
                    minActivePrice = Math.Round(minActivePrice, 2),
                    priceSpread = Math.Round(priceSpread, 2),
                    spreadPercent = Math.Round(spreadPercent, 1),
                    dealsCount
                };
            }).Where(x => x.totalCount > 0).ToList();

            // Overall market stats
            var totalSoldValue = soldProducts.Sum(p => p.Price ?? 0);
            var avgSoldPrice = soldProducts.Any() ? soldProducts.Average(p => (double)p.Price!.Value) : 0;
            var medianSoldPrice = soldProducts.Any() ? GetMedian(soldProducts.Select(p => (double)p.Price!.Value).ToList()) : 0;

            // Sell-through rate (last 7 days)
            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);
            var recentSold = soldProducts.Where(p => p.EndDateUtc.HasValue && p.EndDateUtc >= sevenDaysAgo).Count();
            var recentActive = activeProducts.Count;
            var sellThroughRate = recentActive + recentSold > 0
                ? Math.Round((double)recentSold / (recentActive + recentSold) * 100, 1)
                : 0;

            // Price distribution for sold items (what prices actually sell)
            var priceRanges = new[] { 0, 25, 50, 100, 200, 500, 1000 };
            var priceDistribution = new List<object>();
            for (int i = 0; i < priceRanges.Length; i++)
            {
                var min = priceRanges[i];
                var max = i < priceRanges.Length - 1 ? priceRanges[i + 1] : int.MaxValue;
                var label = max == int.MaxValue ? $"{min}+" : $"{min}-{max}";
                var soldCount = soldProducts.Count(p => p.Price >= min && p.Price < max);
                var activeCount = activeProducts.Count(p => p.Price >= min && p.Price < max);
                priceDistribution.Add(new { range = label, sold = soldCount, active = activeCount });
            }

            // Sales velocity by day (last 30 days)
            var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
            var salesByDay = soldProducts
                .Where(p => p.EndDateUtc.HasValue && p.EndDateUtc >= thirtyDaysAgo)
                .GroupBy(p => p.EndDateUtc!.Value.Date)
                .Select(g => new
                {
                    date = g.Key.ToString("yyyy-MM-dd"),
                    count = g.Count(),
                    avgPrice = Math.Round(g.Average(p => (double)(p.Price ?? 0)), 2),
                    volume = Math.Round(g.Sum(p => p.Price ?? 0), 2)
                })
                .OrderBy(x => x.date)
                .ToList();

            // Best deals (active base_product with highest potential margin)
            var bestDeals = activeProducts
                .Where(p => p.Category == "base_product")
                .Select(p =>
                {
                    var sameCategorySold = soldProducts.Where(x => x.Category == "base_product").ToList();
                    var medianPrice = sameCategorySold.Any() ? GetMedian(sameCategorySold.Select(x => (double)x.Price!.Value).ToList()) : 0;
                    var potentialProfit = medianPrice - (double)(p.Price ?? 0);
                    var profitPercent = p.Price > 0 ? (potentialProfit / (double)p.Price.Value) * 100 : 0;
                    return new
                    {
                        ebayListingId = p.EbayListingId,
                        productName = p.ProductName,
                        title = p.Title,
                        price = p.Price,
                        brand = p.Brand,
                        model = p.Model,
                        category = p.Category,
                        medianSoldPrice = Math.Round(medianPrice, 2),
                        potentialProfit = Math.Round(potentialProfit, 2),
                        profitPercent = Math.Round(profitPercent, 1),
                        url = p.Url
                    };
                })
                .Where(x => x.potentialProfit > 0 && x.profitPercent > 20)
                .OrderByDescending(x => x.profitPercent)
                .Take(10)
                .ToList();

            var metrics = new
            {
                summary = new
                {
                    totalProducts = products.Count,
                    soldProducts = soldProducts.Count,
                    activeProducts = activeProducts.Count,
                    totalMarketValue = Math.Round(totalSoldValue, 2),
                    avgSoldPrice = Math.Round(avgSoldPrice, 2),
                    medianSoldPrice = Math.Round(medianSoldPrice, 2),
                    sellThroughRate7d = sellThroughRate,
                    activeJobs = jobs.Count(j => j.IsEnabled)
                },
                categoryBreakdown,
                brandBreakdown,
                productNameBreakdown,
                arbitrageByJob,
                priceDistribution,
                salesByDay,
                bestDeals
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(metrics);
            return response;
        }

        private static double GetMedian(List<double> values)
        {
            if (!values.Any()) return 0;
            var sorted = values.OrderBy(x => x).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2
                : sorted[mid];
        }

        #endregion

        #region Static Files

        [Function("StaticFiles")]
        public async Task<HttpResponseData> ServeStaticFile(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "static/{*filePath}")]
            HttpRequestData req,
            string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            // Prevent path traversal
            var normalizedPath = filePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(_wwwrootPath, normalizedPath));
            if (!fullPath.StartsWith(_wwwrootPath))
            {
                return req.CreateResponse(HttpStatusCode.Forbidden);
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("Static file not found: {Path}", fullPath);
                return req.CreateResponse(HttpStatusCode.NotFound);
            }

            var contentType = GetContentType(fullPath);
            var content = await File.ReadAllBytesAsync(fullPath);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", contentType);
            response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            await response.Body.WriteAsync(content);
            return response;
        }

        private static string GetContentType(string filePath)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".html" => "text/html; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".ico" => "image/x-icon",
                _ => "application/octet-stream"
            };
        }

        #endregion

        #region Dashboard UI

        [Function("Dashboard")]
        public async Task<HttpResponseData> Dashboard(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")]
            HttpRequestData req)
        {
            var jobs = await _dbContext.ScrapeJobs.ToListAsync();

            // Read the HTML template
            var templatePath = Path.Combine(_wwwrootPath, "dashboard.html");
            if (!File.Exists(templatePath))
            {
                _logger.LogError("Dashboard template not found at: {Path}", templatePath);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Dashboard template not found");
                return errorResponse;
            }

            var template = await File.ReadAllTextAsync(templatePath);

            // Generate jobs list HTML
            var jobsListHtml = GenerateJobsListHtml(jobs);

            // Generate job options for filter dropdown
            var jobOptionsHtml = GenerateJobOptionsHtml(jobs);

            // Generate jobs JSON for JavaScript
            var jobsJson = GenerateJobsJson(jobs);

            // Replace placeholders
            var html = template
                .Replace("{{JOBS_LIST}}", jobsListHtml)
                .Replace("{{JOB_OPTIONS}}", jobOptionsHtml)
                .Replace("{{JOBS_JSON}}", jobsJson);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await response.WriteStringAsync(html);
            return response;
        }

        private string GenerateJobsListHtml(List<ScrapeJob> jobs)
        {
            var sb = new StringBuilder();
            foreach (var job in jobs)
            {
                var enabledBadge = job.IsEnabled
                    ? @"<span class=""badge badge-enabled"">Enabled</span>"
                    : @"<span class=""badge badge-disabled"">Disabled</span>";
                var lastRun = job.LastRunUtc?.ToString("yyyy-MM-dd HH:mm") ?? "Never";
                var toggleBtnText = job.IsEnabled ? "Disable" : "Enable";
                var toggleBtnClass = job.IsEnabled ? "btn-secondary" : "btn-success";

                sb.Append($@"
                <div class=""job-card"" id=""job-{job.Id}"">
                    <div class=""job-info"">
                        <div class=""job-title"">{System.Web.HttpUtility.HtmlEncode(job.SearchTerm)} {enabledBadge}</div>
                        <div class=""job-meta"">
                            Type: {job.SearchType} | Format: {job.BuyingFormat} | Condition: {job.Condition} |
                            Frequency: {job.FrequencyMinutes}min | Last Run: {lastRun}
                        </div>
                    </div>
                    <div class=""job-actions"">
                        <button class=""btn btn-primary"" onclick=""runJob({job.Id})"">Run Now</button>
                        <button class=""btn btn-info"" onclick=""refreshJobStatuses({job.Id})"">Refresh Status</button>
                        <button class=""btn btn-secondary"" onclick=""editJob({job.Id})"">Edit</button>
                        <button class=""btn {toggleBtnClass}"" onclick=""toggleJob({job.Id})"">{toggleBtnText}</button>
                        <button class=""btn btn-danger"" onclick=""deleteJob({job.Id})"">Delete</button>
                    </div>
                </div>");
            }
            return sb.ToString();
        }

        private string GenerateJobOptionsHtml(List<ScrapeJob> jobs)
        {
            var sb = new StringBuilder();
            foreach (var job in jobs)
            {
                sb.Append($@"<option value=""{job.Id}"">{System.Web.HttpUtility.HtmlEncode(job.SearchTerm)}</option>");
            }
            return sb.ToString();
        }

        private string GenerateJobsJson(List<ScrapeJob> jobs)
        {
            var jobDict = jobs.ToDictionary(j => j.Id.ToString(), j => j.SearchTerm);
            var json = JsonSerializer.Serialize(jobDict);
            // HTML-encode for safe embedding in data attribute
            return System.Web.HttpUtility.HtmlEncode(json);
        }

        #endregion
    }
}
