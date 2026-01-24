using AIOMarketMaker.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Utils;

public static class InMemoryDbContextFactory
{
    public static EtlDbContext Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new EtlDbContext(options);
        context.Database.EnsureCreated();

        return context;
    }
}
