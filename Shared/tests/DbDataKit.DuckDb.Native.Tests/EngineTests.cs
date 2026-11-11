using DuckDB.NET.Data;

namespace DbDataKit.DuckDb.Native.Tests;

/// <summary>
/// The engine comes from duckdb.pin through DuckDb.Native.targets, not from a NuGet package (plan 13, W1): these tests fail if the
/// library is not where DuckDB.NET looks, or is not the pinned release.
/// </summary>
public sealed class EngineTests
{
    [Fact]
    public void TheLoadedEngineIsThePinnedRelease()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version()";
        Assert.Equal("v" + PinnedVersion(), (string?)command.ExecuteScalar());
    }

    [Fact]
    public void AQueryRuns()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sum(i) FROM range(1, 11) t(i)";
        Assert.Equal(new System.Numerics.BigInteger(55), (System.Numerics.BigInteger)command.ExecuteScalar()!);   // sum of integers is a HUGEINT
    }

    private static string PinnedVersion()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var pin = Path.Combine(directory.FullName, "duckdb.pin");
            if (!File.Exists(pin)) continue;
            return File.ReadLines(pin).Select(l => l.Trim()).First(l => l.StartsWith("VERSION=", StringComparison.Ordinal))["VERSION=".Length..];
        }
        throw new InvalidOperationException("duckdb.pin was not found above " + AppContext.BaseDirectory);
    }
}
