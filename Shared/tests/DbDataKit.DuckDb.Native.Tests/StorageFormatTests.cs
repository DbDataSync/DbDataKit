using System.Security.Cryptography;
using DuckDB.NET.Data;

namespace DbDataKit.DuckDb.Native.Tests;

/// <summary>
/// DuckDB 2.0 writes a storage format that 1.x cannot read (plan 13, W1 item 4). These tests use a database written by 1.5.6 to pin
/// down what 2.0.0 does to such a file, so no product converts a user's file silently.
/// </summary>
public sealed class StorageFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbdatakit-duckdb-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "orders.duckdb");

    public StorageFormatTests()
    {
        Directory.CreateDirectory(_directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "duckdb-1.5.6.duckdb"), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AVersion1FileOpensReadOnlyAndItsBytesDoNotChange()
    {
        var before = Fingerprint();
        using (var connection = Open("access_mode=READ_ONLY"))
        {
            Assert.Equal(100L, Scalar<long>(connection, "SELECT count(*) FROM orders"));
            Assert.Equal(3.25m, Scalar<decimal>(connection, "SELECT amount FROM orders WHERE id = 1"));
            Assert.Equal("customer-3", Scalar<string>(connection, "SELECT customer FROM orders WHERE id = 3"));
            Assert.Equal("b7", Scalar<string>(connection, "SELECT tags[2] FROM orders WHERE id = 7"));
            Assert.True(Scalar<long>(connection, "SELECT count(*) FROM big_orders") > 0);
        }
        Assert.Equal(before, Fingerprint());
        Assert.Equal([DatabasePath], Directory.GetFiles(_directory));   // no .wal or other file left beside it
    }

    [Fact]
    public void ReadOnlyIsRefusedForWrites()
    {
        using var connection = Open("access_mode=READ_ONLY");
        Assert.ThrowsAny<DuckDBException>(() => Scalar<long>(connection, "CREATE TABLE t(i INTEGER)"));
    }

    [Fact]
    public void AReadWriteOpenThatOnlyReadsLeavesTheFileUntouched()
    {
        var before = Fingerprint();
        using (var connection = Open("access_mode=READ_WRITE"))
            Assert.Equal(100L, Scalar<long>(connection, "SELECT count(*) FROM orders"));
        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public void AWriteToAVersion1FileKeepsItsStorageVersion()
    {
        Assert.Equal((StorageVersion: 64UL, Engine: "v1.5.6"), Header(DatabasePath));
        using (var connection = Open("access_mode=READ_WRITE"))
            Execute(connection, "CREATE TABLE added(i INTEGER); INSERT INTO added VALUES (1)");
        // 2.0 writes into the file in the format it has, so a 1.x engine still reads it (checked once by hand against 1.5.6)
        Assert.Equal((StorageVersion: 64UL, Engine: "v1.5.6"), Header(DatabasePath));
    }

    [Fact]
    public void ANewDatabaseGetsAStorageVersionThatVersion1CannotRead()
    {
        var fresh = Path.Combine(_directory, "fresh.duckdb");
        using (var connection = new DuckDBConnection($"Data Source={fresh}"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE t AS SELECT i FROM range(5) t(i); CHECKPOINT");
        }
        var (storageVersion, engine) = Header(fresh);
        Assert.Equal("v2.0.0", engine);
        Assert.True(storageVersion > 68, $"1.x reads storage versions 64 to 68; this file is {storageVersion}");
    }

    /// <summary>The storage version and the engine version a database file's header records (bytes 12 to 19 and from 52).</summary>
    private static (ulong StorageVersion, string Engine) Header(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[64];
        stream.ReadExactly(header);
        Assert.Equal("DUCK", System.Text.Encoding.ASCII.GetString(header, 8, 4));
        return (BitConverter.ToUInt64(header, 12), System.Text.Encoding.ASCII.GetString(header, 52, 12).TrimEnd('\0'));
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private (string Hash, DateTime Modified) Fingerprint() =>
        (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(DatabasePath))), File.GetLastWriteTimeUtc(DatabasePath));

    private DuckDBConnection Open(string settings)
    {
        var connection = new DuckDBConnection($"Data Source={DatabasePath};{settings}");
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
