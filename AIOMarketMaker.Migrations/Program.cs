using AIOMarketMaker.Core.Data.Migrations;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AIOMarketMaker.Migrations <connection-string>");
    return 1;
}

var connectionString = args[0];
Console.WriteLine("=== AIOMarketMaker SQL Server Migrations ===");

try
{
    var runner = new MigrationRunner(connectionString, null, useSqlServer: true);
    runner.ApplyMigrations();
    Console.WriteLine("Migrations completed successfully.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Migration failed: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}
