using AIOMarketMaker.Core.Data.Migrations;
using Microsoft.Data.SqlClient;

namespace AIOMarketMaker.Console.Tasks;

public class MigrateTask : ITask
{
    public string Name => "migrate";
    public string Description => "Run database migrations on SQL Server";

    public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length < 1)
        {
            System.Console.Error.WriteLine("Usage: dotnet run -- migrate <connection-string> [--dry-run]");
            System.Console.Error.WriteLine();
            System.Console.Error.WriteLine("Options:");
            System.Console.Error.WriteLine("  --dry-run     Show pending migrations without applying them");
            return Task.FromResult(1);
        }

        var connectionString = args[0];
        var dryRun = args.Contains("--dry-run");

        System.Console.WriteLine("=== AIOMarketMaker Migration ===");
        System.Console.WriteLine($"Database: {GetDatabaseName(connectionString)}");
        System.Console.WriteLine($"Mode: {(dryRun ? "Dry Run" : "Apply")}");
        System.Console.WriteLine();

        try
        {
            var runner = new MigrationRunner(connectionString, null, useSqlServer: true);

            if (dryRun)
            {
                System.Console.WriteLine("Dry run mode - no changes will be made");
                // TODO: Add method to list pending migrations without applying
            }
            else
            {
                runner.ApplyMigrations();
                System.Console.WriteLine();
                System.Console.WriteLine("Migrations completed successfully!");
            }

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"Migration failed: {ex.Message}");
            System.Console.Error.WriteLine(ex.ToString());
            return Task.FromResult(1);
        }
    }

    private static string GetDatabaseName(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return $"{builder.DataSource}/{builder.InitialCatalog}";
        }
        catch
        {
            return "(unable to parse)";
        }
    }
}
