using AIOMarketMaker.Core.Data.Migrations;

// Simple CLI for running database migrations
// Usage: dotnet run -- --connection "Server=...;Database=...;..."

if (args.Length < 2 || args[0] != "--connection")
{
    Console.Error.WriteLine("Usage: AIOMarketMaker.MigrationCli --connection \"<connection-string>\"");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --connection  SQL Server connection string (required)");
    Console.Error.WriteLine("  --dry-run     Show pending migrations without applying them");
    Environment.Exit(1);
}

var connectionString = args[1];
var dryRun = args.Contains("--dry-run");

Console.WriteLine("=== AIOMarketMaker Migration CLI ===");
Console.WriteLine($"Database: {GetDatabaseName(connectionString)}");
Console.WriteLine($"Mode: {(dryRun ? "Dry Run" : "Apply")}");
Console.WriteLine();

try
{
    var runner = new MigrationRunner(connectionString, null, useSqlServer: true);

    if (dryRun)
    {
        Console.WriteLine("Dry run mode - no changes will be made");
        // TODO: Add method to list pending migrations without applying
    }
    else
    {
        runner.ApplyMigrations();
        Console.WriteLine();
        Console.WriteLine("Migrations completed successfully!");
    }

    Environment.Exit(0);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(1);
}

static string GetDatabaseName(string connectionString)
{
    try
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        return $"{builder.DataSource}/{builder.InitialCatalog}";
    }
    catch
    {
        return "(unable to parse)";
    }
}
