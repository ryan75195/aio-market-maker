using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Data;

public static class DbQueryHelper
{
    public static async Task<List<T>> ExecuteQuery<T>(
        EtlDbContext db, string sql, Func<DbDataReader, T> map)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        var results = new List<T>();
        while (await reader.ReadAsync())
        {
            results.Add(map(reader));
        }

        return results;
    }

    public static async Task<object?> ExecuteScalar(EtlDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    public static decimal SafeGetDecimal(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0m;
        }

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(double))
        {
            return (decimal)reader.GetDouble(ordinal);
        }

        return reader.GetDecimal(ordinal);
    }

    public static IEnumerable<int> ParseJobIds(string? jobIds)
    {
        if (string.IsNullOrWhiteSpace(jobIds))
        {
            return Enumerable.Empty<int>();
        }

        return jobIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value);
    }
}
