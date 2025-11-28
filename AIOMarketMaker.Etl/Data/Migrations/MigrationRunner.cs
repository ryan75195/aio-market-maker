using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Etl.Data.Migrations;

/// <summary>
/// Simple SQL migration runner for SQLite.
/// Tracks applied migrations in a __MigrationHistory table.
/// </summary>
public class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner>? _logger;

    public MigrationRunner(string connectionString, ILogger<MigrationRunner>? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Applies all pending migrations from the Migrations folder.
    /// </summary>
    public void ApplyMigrations()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        EnsureMigrationHistoryTable(connection);

        var appliedMigrations = GetAppliedMigrations(connection);
        var migrationFiles = GetMigrationFiles();

        foreach (var migrationFile in migrationFiles.OrderBy(f => f))
        {
            var migrationName = Path.GetFileNameWithoutExtension(migrationFile);

            if (appliedMigrations.Contains(migrationName))
            {
                _logger?.LogDebug("Migration {Migration} already applied, skipping", migrationName);
                continue;
            }

            _logger?.LogInformation("Applying migration: {Migration}", migrationName);

            var sql = File.ReadAllText(migrationFile);
            ExecuteMigration(connection, sql, migrationName);

            _logger?.LogInformation("Migration {Migration} applied successfully", migrationName);
        }
    }

    private void EnsureMigrationHistoryTable(SqliteConnection connection)
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS __MigrationHistory (
                MigrationId TEXT PRIMARY KEY,
                AppliedUtc TEXT NOT NULL DEFAULT (datetime('now'))
            );";

        using var command = new SqliteCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private HashSet<string> GetAppliedMigrations(SqliteConnection connection)
    {
        var migrations = new HashSet<string>();

        const string sql = "SELECT MigrationId FROM __MigrationHistory";
        using var command = new SqliteCommand(sql, connection);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private void ExecuteMigration(SqliteConnection connection, string sql, string migrationName)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            // Execute the migration SQL
            using (var command = new SqliteCommand(sql, connection, transaction))
            {
                command.ExecuteNonQuery();
            }

            // Record the migration
            const string insertSql = "INSERT INTO __MigrationHistory (MigrationId) VALUES (@migrationId)";
            using (var command = new SqliteCommand(insertSql, connection, transaction))
            {
                command.Parameters.AddWithValue("@migrationId", migrationName);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private IEnumerable<string> GetMigrationFiles()
    {
        // Look for migration files in the Migrations folder relative to the assembly
        var assemblyLocation = Path.GetDirectoryName(typeof(MigrationRunner).Assembly.Location);
        var migrationsPath = Path.Combine(assemblyLocation!, "Data", "Migrations");

        if (!Directory.Exists(migrationsPath))
        {
            _logger?.LogWarning("Migrations folder not found at {Path}", migrationsPath);
            return Enumerable.Empty<string>();
        }

        return Directory.GetFiles(migrationsPath, "*.sql");
    }
}
