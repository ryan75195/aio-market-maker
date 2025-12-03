using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Services;
using AIOMarketMaker.Services.Dtos;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Controllers
{
    public class DashboardController
    {
        private readonly EtlDbContext _dbContext;
        private readonly IJobService _jobService;
        private readonly IListingService _listingService;
        private readonly IProductService _productService;
        private readonly IMetricsService _metricsService;
        private readonly ILogger<DashboardController> _logger;
        private static readonly string _wwwrootPath;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        static DashboardController()
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? ".";
            _wwwrootPath = Path.Combine(assemblyDir, "wwwroot");
        }

        public DashboardController(
            EtlDbContext dbContext,
            IJobService jobService,
            IListingService listingService,
            IProductService productService,
            IMetricsService metricsService,
            ILogger<DashboardController> logger)
        {
            _dbContext = dbContext;
            _jobService = jobService;
            _listingService = listingService;
            _productService = productService;
            _metricsService = metricsService;
            _logger = logger;
        }

        #region Jobs API

        [Function("GetJobs")]
        public async Task<HttpResponseData> GetJobs(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "jobs")]
            HttpRequestData req)
        {
            var jobs = await _jobService.GetAllJobsAsync();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, jobs);
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
                request = JsonSerializer.Deserialize<CreateJobRequest>(body, _jsonOptions);
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

            var job = await _jobService.CreateJobAsync(request);
            var response = req.CreateResponse(HttpStatusCode.Created);
            await WriteJsonAsync(response, job);
            return response;
        }

        [Function("DeleteJob")]
        public async Task<HttpResponseData> DeleteJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "jobs/{id:int}")]
            HttpRequestData req,
            int id)
        {
            var result = await _jobService.DeleteJobAsync(id);
            if (result == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { message = $"Job {id} deleted along with {result.ListingsDeleted} listings" });
            return response;
        }

        [Function("ToggleJob")]
        public async Task<HttpResponseData> ToggleJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "jobs/{id:int}/toggle")]
            HttpRequestData req,
            int id)
        {
            var result = await _jobService.ToggleJobAsync(id);
            if (result == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { id, isEnabled = result.Value.isEnabled });
            return response;
        }

        [Function("UpdateJob")]
        public async Task<HttpResponseData> UpdateJob(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "jobs/{id:int}")]
            HttpRequestData req,
            int id)
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
                request = JsonSerializer.Deserialize<CreateJobRequest>(body, _jsonOptions);
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

            var job = await _jobService.UpdateJobAsync(id, request);
            if (job == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Job {id} not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, job);
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
            var filter = new ListingFilter(
                Page: int.TryParse(query["page"], out var p) ? p : 1,
                PageSize: int.TryParse(query["pageSize"], out var ps) ? ps : 50,
                Status: query["status"],
                JobId: int.TryParse(query["jobId"], out var jid) ? jid : null,
                Search: query["search"]
            );

            var result = await _listingService.GetListingsAsync(filter);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, new { total = result.Total, page = result.Page, pageSize = result.PageSize, listings = result.Items });
            return response;
        }

        [Function("GetListingDetails")]
        public async Task<HttpResponseData> GetListingDetails(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "listings/{id:int}/details")]
            HttpRequestData req,
            int id)
        {
            var details = await _listingService.GetListingDetailsAsync(id);
            if (details == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"Listing {id} not found" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, details);
            return response;
        }

        #endregion

        #region Products API

        [Function("GetProducts")]
        public async Task<HttpResponseData> GetProducts(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var filter = new ProductFilter(
                Page: int.TryParse(query["page"], out var p) ? p : 1,
                PageSize: int.TryParse(query["pageSize"], out var ps) ? ps : 50,
                Category: query["category"],
                Brand: query["brand"],
                Model: query["model"],
                ProductName: query["productName"],
                Status: query["status"],
                Search: query["search"],
                Edition: query["edition"],
                StorageCapacity: query["storageCapacity"],
                Color: query["color"]
            );

            var result = await _productService.GetProductsAsync(filter);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, new { total = result.Total, page = result.Page, pageSize = result.PageSize, products = result.Items });
            return response;
        }

        [Function("GetProductNames")]
        public async Task<HttpResponseData> GetProductNames(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/names")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var category = query["category"];

            var productNames = await _productService.GetProductNamesAsync(category);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, productNames);
            return response;
        }

        [Function("GetProductVariants")]
        public async Task<HttpResponseData> GetProductVariants(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "products/variants")]
            HttpRequestData req)
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var productName = query["productName"];

            if (string.IsNullOrEmpty(productName))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "productName query parameter is required" });
                return badRequest;
            }

            var variants = await _productService.GetProductVariantsAsync(productName);
            if (variants == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(new { error = $"No products found with name '{productName}'" });
                return notFound;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, variants);
            return response;
        }

        #endregion

        #region Metrics API

        [Function("GetMetrics")]
        public async Task<HttpResponseData> GetMetrics(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics")]
            HttpRequestData req)
        {
            var metrics = await _metricsService.GetDashboardMetricsAsync();
            var response = req.CreateResponse(HttpStatusCode.OK);
            await WriteJsonAsync(response, metrics);
            return response;
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

        private static async Task WriteJsonAsync<T>(HttpResponseData response, T data)
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await response.WriteStringAsync(json);
        }

        #endregion

        #region Dashboard UI

        [Function("Dashboard")]
        public async Task<HttpResponseData> Dashboard(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")]
            HttpRequestData req)
        {
            var jobs = await _dbContext.ScrapeJobs.ToListAsync();

            var templatePath = Path.Combine(_wwwrootPath, "dashboard.html");
            if (!File.Exists(templatePath))
            {
                _logger.LogError("Dashboard template not found at: {Path}", templatePath);
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteStringAsync("Dashboard template not found");
                return errorResponse;
            }

            var template = await File.ReadAllTextAsync(templatePath);

            var jobsListHtml = GenerateJobsListHtml(jobs);
            var jobOptionsHtml = GenerateJobOptionsHtml(jobs);
            var jobsJson = GenerateJobsJson(jobs);

            var html = template
                .Replace("{{JOBS_LIST}}", jobsListHtml)
                .Replace("{{JOB_OPTIONS}}", jobOptionsHtml)
                .Replace("{{JOBS_JSON}}", jobsJson);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html; charset=utf-8");
            await response.WriteStringAsync(html);
            return response;
        }

        private string GenerateJobsListHtml(List<Etl.Data.Models.ScrapeJob> jobs)
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

        private string GenerateJobOptionsHtml(List<Etl.Data.Models.ScrapeJob> jobs)
        {
            var sb = new StringBuilder();
            foreach (var job in jobs)
            {
                sb.Append($@"<option value=""{job.Id}"">{System.Web.HttpUtility.HtmlEncode(job.SearchTerm)}</option>");
            }
            return sb.ToString();
        }

        private string GenerateJobsJson(List<Etl.Data.Models.ScrapeJob> jobs)
        {
            var jobDict = jobs.ToDictionary(j => j.Id.ToString(), j => j.SearchTerm);
            var json = JsonSerializer.Serialize(jobDict);
            return System.Web.HttpUtility.HtmlEncode(json);
        }

        #endregion
    }
}
