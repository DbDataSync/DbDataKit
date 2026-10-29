using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>The table picker lists the base tables of the database, in any schema of the user's: not views, and not the engine's own (plan 13, A11).</summary>
[Trait("Category", "Integration")]
public sealed class PostgresTableListingTests(PostgresTestDatabase db) : IClassFixture<PostgresTestDatabase>
{
    [Fact]
    public async Task ListDatabasesAsync_OffersTheTestDatabase_NotTemplates()
    {
        await using var connection = db.OpenConnection();
        var databases = await new PostgresDriver().ListDatabasesAsync(connection, CancellationToken.None);
        Assert.Contains(db.DatabaseName, databases);
        Assert.DoesNotContain(databases, d => d is "template0" or "template1");
    }

    [Fact]
    public async Task ListTablesAsync_OffersBaseTablesOfAnySchema_NotViewsOrTheEnginesOwn()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = $"s_{suffix}";
        await using var connection = db.OpenConnection();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}"."other" (id int primary key);
                CREATE VIEW "{schema}"."v_other" AS SELECT id FROM "{schema}"."other";
                """;
            await create.ExecuteNonQueryAsync();
        }

        var tables = await new PostgresDriver().ListTablesAsync(connection, db.DatabaseName, CancellationToken.None);

        Assert.Contains(tables, t => t.Schema == schema && t.Table == "other");
        Assert.DoesNotContain(tables, t => t.Table == "v_other");
        Assert.DoesNotContain(tables, t => t.Schema is "pg_catalog" or "information_schema");
    }
}
