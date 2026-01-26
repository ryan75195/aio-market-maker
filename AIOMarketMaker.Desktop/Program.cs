using System.Diagnostics;

var electronDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "electron");
if (!Directory.Exists(electronDir))
{
    electronDir = Path.Combine(Directory.GetCurrentDirectory(), "electron");
}

electronDir = Path.GetFullPath(electronDir);

if (!Directory.Exists(electronDir))
{
    Console.WriteLine($"Error: electron directory not found at {electronDir}");
    return 1;
}

Console.WriteLine($"Electron app directory: {electronDir}");

// Check if node_modules exists
var nodeModulesPath = Path.Combine(electronDir, "node_modules");
if (!Directory.Exists(nodeModulesPath))
{
    Console.WriteLine("Installing npm dependencies...");
    var npmInstall = new ProcessStartInfo
    {
        FileName = "npm",
        Arguments = "install",
        WorkingDirectory = electronDir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    using var installProcess = Process.Start(npmInstall);
    if (installProcess == null)
    {
        Console.WriteLine("Error: Failed to start npm install");
        return 1;
    }

    installProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
    installProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
    installProcess.BeginOutputReadLine();
    installProcess.BeginErrorReadLine();
    await installProcess.WaitForExitAsync();

    if (installProcess.ExitCode != 0)
    {
        Console.WriteLine($"npm install failed with exit code {installProcess.ExitCode}");
        return 1;
    }
}

// Check if config.json exists
var configPath = Path.Combine(electronDir, "config.json");
var configExamplePath = Path.Combine(electronDir, "config.example.json");
if (!File.Exists(configPath) && File.Exists(configExamplePath))
{
    Console.WriteLine("Creating config.json from config.example.json...");
    Console.WriteLine("Please edit config.json with your API keys.");
    File.Copy(configExamplePath, configPath);
}

Console.WriteLine("Starting Electron app...");

var npmStart = new ProcessStartInfo
{
    FileName = "npm",
    Arguments = "run dev",
    WorkingDirectory = electronDir,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};

using var process = Process.Start(npmStart);
if (process == null)
{
    Console.WriteLine("Error: Failed to start Electron app");
    return 1;
}

process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
process.BeginOutputReadLine();
process.BeginErrorReadLine();

await process.WaitForExitAsync();
return process.ExitCode;
