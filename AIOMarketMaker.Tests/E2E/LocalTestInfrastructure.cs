using System.Diagnostics;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

/// <summary>
/// Manages local test infrastructure: Azurite, Azure Functions API, ScraperWorker.
/// </summary>
public class LocalTestInfrastructure : IDisposable
{
    private Process? _azuriteProcess;
    private Process? _functionsProcess;
    private Process? _workerProcess;

    public const int AzuritePort = 10000;      // Blob
    public const int AzuriteQueuePort = 10001; // Queue
    public const int AzuriteTablePort = 10002; // Table
    public const int FunctionsPort = 7071;
    public const int WorkerPort = 5000;        // Not used in queue mode, but reserved

    /// <summary>
    /// Starts Azurite using Docker.
    /// </summary>
    public async Task StartAzuriteAsync()
    {
        if (IsPortInUse(AzuritePort))
        {
            Console.WriteLine("Azurite already running on port 10000");
            return;
        }

        _azuriteProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "run --rm -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start Docker process for Azurite");

        // Wait for Azurite to be ready
        await WaitForPortAsync(AzuritePort, TimeSpan.FromSeconds(30));
        Console.WriteLine("Azurite started");
    }

    /// <summary>
    /// Starts Azure Functions API pointing to Azurite.
    /// </summary>
    public async Task StartFunctionsApiAsync(string projectPath)
    {
        if (IsPortInUse(FunctionsPort))
        {
            Console.WriteLine("Functions API already running on port 7071");
            return;
        }

        var settingsPath = Path.Combine(projectPath, "local.settings.local.json");
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException($"Local settings not found: {settingsPath}");
        }

        // Copy local settings to local.settings.json for func to pick up
        var targetPath = Path.Combine(projectPath, "local.settings.json");
        File.Copy(settingsPath, targetPath, overwrite: true);

        _functionsProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "func",
            Arguments = "start",
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start Azure Functions process");

        await WaitForPortAsync(FunctionsPort, TimeSpan.FromSeconds(60));
        Console.WriteLine("Azure Functions API started");
    }

    /// <summary>
    /// Starts ScraperWorker in simple queue mode pointing to Azurite.
    /// </summary>
    public async Task StartWorkerAsync(string projectPath)
    {
        _workerProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "run",
            WorkingDirectory = projectPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Local"
            }
        }) ?? throw new InvalidOperationException("Failed to start ScraperWorker process");

        // Give worker time to start and connect to queue
        await Task.Delay(5000);
        Console.WriteLine("ScraperWorker started in queue mode");
    }

    public void Dispose()
    {
        StopProcess(_workerProcess);
        StopProcess(_functionsProcess);
        StopProcess(_azuriteProcess);
    }

    private static void StopProcess(Process? process)
    {
        if (process != null && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            process.Dispose();
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            // Use 127.0.0.1 instead of localhost for more reliable Windows behavior
            var result = client.BeginConnect("127.0.0.1", port, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
            if (success)
            {
                try
                {
                    client.EndConnect(result);
                    return true;
                }
                catch
                {
                    // EndConnect can throw if connection was refused
                    return false;
                }
            }
            return false;
        }
        catch
        {
            // Any connection error means port is not available
            return false;
        }
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (IsPortInUse(port))
                return;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Port {port} not available after {timeout.TotalSeconds}s");
    }
}
