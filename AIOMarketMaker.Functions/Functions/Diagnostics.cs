using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Diagnostic endpoint for debugging database issues.
/// </summary>
public class Diagnostics
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<Diagnostics> _logger;

    public Diagnostics(EtlDbContext dbContext, ILogger<Diagnostics> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function("Diagnostics")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "diagnostics")] HttpRequestData req)
    {
        var diagnostics = new DiagnosticsResult();

        try
        {
            // Test database connection
            diagnostics.CanConnect = await _dbContext.Database.CanConnectAsync();
            diagnostics.ConnectionSuccess = "Database connection successful";

            if (diagnostics.CanConnect)
            {
                // Try to get tables
                try
                {
                    var tables = await _dbContext.Database
                        .SqlQueryRaw<string>("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'")
                        .ToListAsync();
                    diagnostics.Tables = tables;
                }
                catch (Exception ex)
                {
                    diagnostics.TablesError = ex.Message;
                }

                // Try to count jobs
                try
                {
                    diagnostics.JobCount = await _dbContext.ScrapeJobs.CountAsync();
                }
                catch (Exception ex)
                {
                    diagnostics.JobCountError = ex.Message;
                }

                // Try to list migration history
                try
                {
                    var migrations = await _dbContext.Database
                        .SqlQueryRaw<string>("SELECT MigrationId FROM __MigrationHistory ORDER BY MigrationId")
                        .ToListAsync();
                    diagnostics.AppliedMigrations = migrations;
                }
                catch (Exception ex)
                {
                    diagnostics.MigrationsError = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.CanConnect = false;
            diagnostics.ConnectionError = ex.ToString();
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }
}

public class DiagnosticsResult
{
    public bool CanConnect { get; set; }
    public string? ConnectionSuccess { get; set; }
    public string? ConnectionError { get; set; }
    public List<string>? Tables { get; set; }
    public string? TablesError { get; set; }
    public int? JobCount { get; set; }
    public string? JobCountError { get; set; }
    public List<string>? AppliedMigrations { get; set; }
    public string? MigrationsError { get; set; }
}
