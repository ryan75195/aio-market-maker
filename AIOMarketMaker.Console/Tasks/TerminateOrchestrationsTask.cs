using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AIOMarketMaker.Console.Tasks;

public class TerminateOrchestrationsTask : ITask
{
    private readonly IConfiguration _configuration;

    public string Name => "terminate";
    public string Description => "Terminate all running Azure Durable Function orchestrations";

    public TerminateOrchestrationsTask(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var baseUrl = _configuration.GetValue<string>("FunctionsApi:BaseUrl")
            ?? "https://func-aiomarketmaker-dev.azurewebsites.net/api";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        System.Console.WriteLine("=== Terminate All Running Orchestrations ===");
        System.Console.WriteLine($"API: {baseUrl}");
        System.Console.WriteLine();

        var totalTerminated = 0;
        var totalFailed = 0;
        var iteration = 0;

        while (true)
        {
            iteration++;
            System.Console.WriteLine($"Iteration {iteration}: Fetching orchestrations...");

            HttpResponseMessage statusResponse;
            try
            {
                statusResponse = await http.GetAsync($"{baseUrl}/status", ct);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"Failed to connect: {ex.Message}");
                System.Console.WriteLine("Waiting 5s before retry...");
                await Task.Delay(5000, ct);
                continue;
            }

            if (!statusResponse.IsSuccessStatusCode)
            {
                System.Console.WriteLine($"Status endpoint returned {statusResponse.StatusCode}");
                System.Console.WriteLine("Waiting 5s before retry...");
                await Task.Delay(5000, ct);
                continue;
            }

            var json = await statusResponse.Content.ReadAsStringAsync(ct);
            var status = JsonSerializer.Deserialize<StatusResponse>(json);

            if (status?.orchestrations == null || status.orchestrations.Count == 0)
            {
                System.Console.WriteLine("No orchestrations found.");
                break;
            }

            var running = status.orchestrations
                .Where(o => o.status == "Running" || o.status == "Pending")
                .ToList();

            System.Console.WriteLine($"Found {running.Count} running/pending in this batch");

            if (running.Count == 0)
            {
                System.Console.WriteLine("No more running orchestrations.");
                break;
            }

            foreach (var orch in running)
            {
                try
                {
                    var response = await http.DeleteAsync($"{baseUrl}/orchestration/{orch.instanceId}", ct);
                    if (response.IsSuccessStatusCode)
                        totalTerminated++;
                    else
                        totalFailed++;
                }
                catch
                {
                    totalFailed++;
                }
            }

            System.Console.WriteLine($"Progress: {totalTerminated} terminated, {totalFailed} failed");
            await Task.Delay(500, ct);
        }

        System.Console.WriteLine();
        System.Console.WriteLine($"=== Complete ===");
        System.Console.WriteLine($"Total Terminated: {totalTerminated}");
        System.Console.WriteLine($"Total Failed: {totalFailed}");

        System.Console.WriteLine();
        System.Console.WriteLine("Purging terminated orchestrations...");
        var purgeResponse = await http.PostAsync($"{baseUrl}/orchestration/purge", null, ct);
        if (purgeResponse.IsSuccessStatusCode)
        {
            var purgeJson = await purgeResponse.Content.ReadAsStringAsync(ct);
            System.Console.WriteLine($"Purge result: {purgeJson}");
        }

        return 0;
    }

    private record Orchestration(string instanceId, string name, string status);
    private record StatusResponse(List<Orchestration> orchestrations);
}
