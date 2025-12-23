using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Data.Migrations;

/// <summary>
/// SQL migration runner that supports both SQLite and SQL Server.
/// Tracks applied migrations in a __MigrationHistory table.
/// </summary>
public class MigrationRunner
{
    private readonly string _connectionString;
    private readonly ILogger<MigrationRunner>? _logger;
    private readonly bool _useSqlServer;

    public MigrationRunner(
        string connectionString,
        ILogger<MigrationRunner>? logger = null,
        bool useSqlServer = false)
    {
        _connectionString = connectionString;
        _logger = logger;
        _useSqlServer = useSqlServer;
    }

    /// <summary>
    /// Applies all pending migrations from the Migrations folder.
    /// </summary>
    public void ApplyMigrations()
    {
        using var connection = CreateConnection();
        connection.Open();

        EnsureMigrationHistoryTable(connection);

        var appliedMigrations = GetAppliedMigrations(connection);
        var migrations = GetMigrations().ToList();

        _logger?.LogInformation("Found {Count} embedded migrations", migrations.Count);

        foreach (var (migrationName, sql) in migrations)
        {
            if (appliedMigrations.Contains(migrationName))
            {
                _logger?.LogDebug("Migration {Migration} already applied, skipping", migrationName);
                continue;
            }

            _logger?.LogInformation("Applying migration: {Migration}", migrationName);

            var convertedSql = sql;
            // Convert SQLite syntax to SQL Server if needed
            if (_useSqlServer)
            {
                convertedSql = ConvertToSqlServer(sql);
            }

            ExecuteMigration(connection, convertedSql, migrationName);

            _logger?.LogInformation("Migration {Migration} applied successfully", migrationName);
        }
    }

    private DbConnection CreateConnection()
    {
        return _useSqlServer
            ? new SqlConnection(_connectionString)
            : new SqliteConnection(_connectionString);
    }

    private void EnsureMigrationHistoryTable(DbConnection connection)
    {
        var sql = _useSqlServer
            ? @"IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '__MigrationHistory')
                CREATE TABLE __MigrationHistory (
                    MigrationId NVARCHAR(255) PRIMARY KEY,
                    AppliedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE()
                );"
            : @"CREATE TABLE IF NOT EXISTS __MigrationHistory (
                    MigrationId TEXT PRIMARY KEY,
                    AppliedUtc TEXT NOT NULL DEFAULT (datetime('now'))
                );";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private HashSet<string> GetAppliedMigrations(DbConnection connection)
    {
        var migrations = new HashSet<string>();

        const string sql = "SELECT MigrationId FROM __MigrationHistory";
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private void ExecuteMigration(DbConnection connection, string sql, string migrationName)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            // Execute the migration SQL
            // Split by GO statements for SQL Server batch separation
            var batches = _useSqlServer
                ? SplitSqlBatches(sql)
                : new[] { sql };

            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                command.ExecuteNonQuery();
            }

            // Record the migration
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = _useSqlServer
                    ? "INSERT INTO __MigrationHistory (MigrationId) VALUES (@migrationId)"
                    : "INSERT INTO __MigrationHistory (MigrationId) VALUES (@migrationId)";

                var param = command.CreateParameter();
                param.ParameterName = "@migrationId";
                param.Value = migrationName;
                command.Parameters.Add(param);

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

    /// <summary>
    /// Converts SQLite-specific SQL syntax to SQL Server syntax.
    /// </summary>
    private static string ConvertToSqlServer(string sql)
    {
        var converted = sql;

        // First, find all columns used in indexes (can't be NVARCHAR(MAX))
        // Match both CREATE INDEX and CREATE UNIQUE INDEX
        var indexedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var indexMatches = System.Text.RegularExpressions.Regex.Matches(
            sql,
            @"CREATE\s+(?:UNIQUE\s+)?INDEX.*?\(([^)]+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match match in indexMatches)
        {
            var columns = match.Groups[1].Value.Split(',');
            foreach (var col in columns)
            {
                indexedColumns.Add(col.Trim());
            }
        }

        // INTEGER PRIMARY KEY AUTOINCREMENT -> INT IDENTITY(1,1) PRIMARY KEY
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"INTEGER\s+PRIMARY\s+KEY\s+AUTOINCREMENT",
            "INT IDENTITY(1,1) PRIMARY KEY",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // INTEGER -> INT
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"\bINTEGER\b",
            "INT",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // REAL -> FLOAT
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"\bREAL\b",
            "FLOAT",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // BLOB -> VARBINARY(MAX)
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"\bBLOB\b",
            "VARBINARY(MAX)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // datetime('now') -> GETUTCDATE()
        converted = converted.Replace("datetime('now')", "GETUTCDATE()");

        // ALTER TABLE X RENAME TO Y -> EXEC sp_rename 'X', 'Y' (SQL Server syntax)
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"ALTER\s+TABLE\s+(\w+)\s+RENAME\s+TO\s+(\w+)",
            "EXEC sp_rename '$1', '$2'",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // TEXT -> NVARCHAR(450) for indexed columns, NVARCHAR(MAX) for others
        // Match column definitions: ColumnName TEXT
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"(\w+)\s+TEXT\b",
            match =>
            {
                var colName = match.Groups[1].Value;
                var nvarcharType = indexedColumns.Contains(colName) ? "NVARCHAR(450)" : "NVARCHAR(MAX)";
                return $"{colName} {nvarcharType}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // CREATE TABLE IF NOT EXISTS -> IF NOT EXISTS pattern
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+(\w+)",
            "IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '$1') CREATE TABLE $1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // CREATE [UNIQUE] INDEX IF NOT EXISTS -> IF NOT EXISTS pattern
        converted = System.Text.RegularExpressions.Regex.Replace(
            converted,
            @"CREATE\s+(UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+(\w+)\s+ON\s+(\w+)",
            match =>
            {
                var unique = match.Groups[1].Success ? "UNIQUE " : "";
                var indexName = match.Groups[2].Value;
                var tableName = match.Groups[3].Value;
                return $"IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = '{indexName}' AND object_id = OBJECT_ID('{tableName}')) CREATE {unique}INDEX {indexName} ON {tableName}";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return converted;
    }

    /// <summary>
    /// Splits SQL into batches separated by GO statements.
    /// </summary>
    private static string[] SplitSqlBatches(string sql)
    {
        return System.Text.RegularExpressions.Regex.Split(
            sql,
            @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private IEnumerable<(string Name, string Sql)> GetMigrations()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n);

        foreach (var resourceName in resourceNames)
        {
            // Extract migration name from resource
            // e.g., "AIOMarketMaker.Core.Data.Migrations.001_InitialCreate.sql" -> "001_InitialCreate"
            var parts = resourceName.Split('.');
            // Format is: Namespace.Path.MigrationName.sql, so migration name is second to last
            var migrationName = parts.Length >= 2 ? parts[^2] : resourceName;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger?.LogWarning("Could not read embedded resource: {Resource}", resourceName);
                continue;
            }

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            yield return (migrationName, sql);
        }
    }
}
