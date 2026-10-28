using System.Diagnostics;
using System.Data.Common;
using DbDataSync.Benchmarks;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;
using Microsoft.Data.SqlClient;
using Npgsql;

// The baseline for Sync's staging paths (plan 13, A13): how fast and how heavy it is to stage a batch of changed rows into the target database today.
// A14 and A15 move SQL Server's SqlBulkCopy staging and PostgreSQL's binary COPY staging onto DbDataKit.Transfer.*; their done-when is "within 5% of this".

var args_ = Environment.GetCommandLineArgs();
string? Arg(string name)
{
    var i = Array.IndexOf(args_, name);
    return i >= 0 && i + 1 < args_.Length ? args_[i + 1] : null;
}
bool Flag(string name) => Array.IndexOf(args_, name) >= 0;

if (Flag("--help") || Flag("-h"))
{
    Console.WriteLine("""
        DbDataSync staging benchmark — stages a batch of changed rows through each of Sync's staging providers.

        Usage: tools/benchmarks-staging [options]

          --rows N         rows to stage                       (default 200000)
          --columns N      columns per row                     (default 20)
          --repeat N       runs per provider; the median is reported (default 3)
          --mssql S        SQL Server connection string        (default $DBKIT_TEST_MSSQL, else the shared testenv instance)
          --postgres S     PostgreSQL connection string        (default $DBKIT_TEST_POSTGRES, else the shared testenv instance)
          --only NAME      run one provider: mssql-bulk-copy | mssql-batch-insert | postgres-copy | postgres-batch-insert

        Each run is its own process (peak working set belongs to that run alone) against a scratch database that is created and dropped here.
        Every run verifies the staging table holds the rows it was given.
        """);
    return 0;
}

var rows = int.Parse(Arg("--rows") ?? "200000");
var columns = int.Parse(Arg("--columns") ?? "20");
var repeat = int.Parse(Arg("--repeat") ?? "3");
var mssql = Arg("--mssql") ?? Environment.GetEnvironmentVariable("DBKIT_TEST_MSSQL")
    ?? "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True";
var postgres = Arg("--postgres") ?? Environment.GetEnvironmentVariable("DBKIT_TEST_POSTGRES")
    ?? "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres";

string[] providers = ["mssql-bulk-copy", "mssql-batch-insert", "postgres-copy", "postgres-batch-insert"];

// ---- child mode: one provider, one run ---------------------------------------------------------------------------
if (Arg("--provider") is { } providerName)
{
    var database = Arg("--database")!;
    var isMsSql = providerName.StartsWith("mssql", StringComparison.Ordinal);
    var table = $"Stage_{providerName.Replace('-', '_')}_{Guid.NewGuid():N}";

    await using DbConnection connection = isMsSql
        ? new SqlConnection(new SqlConnectionStringBuilder(mssql) { InitialCatalog = database }.ConnectionString)
        : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres) { Database = database }.ConnectionString);
    await connection.OpenAsync();
    await ExecuteAsync(connection, isMsSql ? CreateSqlServerTable(table, columns) : CreatePostgresTable(table, columns));

    IStagingProvider provider = providerName switch
    {
        "mssql-bulk-copy" => new MsSqlDriver().StagingProviders.OfType<MsSqlStagingTableProvider>().Single(),
        "mssql-batch-insert" => new MsSqlDriver().StagingProviders.OfType<BatchInsertStagingProvider>().Single(),
        "postgres-copy" => new PostgresDriver().StagingProviders.OfType<PgCopyStagingProvider>().Single(),
        "postgres-batch-insert" => new PostgresDriver().StagingProviders.OfType<BatchInsertStagingProvider>().Single(),
        _ => throw new ArgumentOutOfRangeException(nameof(providerName), providerName, null),
    };

    var target = new TableRef { ConnectionName = "bench", Database = database, Schema = isMsSql ? "dbo" : "public", Table = table };
    var mappings = Enumerable.Range(0, columns).Select(c => new ColumnMapping { SourceColumn = BenchmarkSchema.NameOf(c), TargetColumn = BenchmarkSchema.NameOf(c) }).ToList();
    var cached = Enumerable.Range(0, columns).Select(c => new CachedColumn(BenchmarkSchema.NameOf(c), isMsSql ? SqlServerType(c) : PostgresType(c), true, false, false)).ToList();
    var schema = new ChangeSchema(Enumerable.Range(0, columns).Select(BenchmarkSchema.NameOf).ToList());

    StagedChangeSet? staged = null;
    var measurement = await Measured.RunAsync(providerName, async () =>
    {
        staged = await provider.StageAsync(connection, target, Generate(schema, rows, columns), mappings, "bench", cached,
            new Dictionary<string, string>(), CancellationToken.None);
    });

    if (staged!.RowCount != rows)
        throw new InvalidOperationException($"{providerName}: the provider reported {staged.RowCount} staged rows, expected {rows}");
    await using (var count = connection.CreateCommand())
    {
        count.CommandText = $"SELECT COUNT(*) FROM {staged.StagingLocation}";
        var stored = Convert.ToInt64(await count.ExecuteScalarAsync());
        if (stored != rows) throw new InvalidOperationException($"{providerName}: the staging table holds {stored} rows, expected {rows}");
    }
    await provider.CleanupAsync(connection, staged, CancellationToken.None);

    Console.WriteLine(measurement.ToWire());
    return 0;
}

// ---- orchestrator ------------------------------------------------------------------------------------------------
var selected = Arg("--only") is { } only ? providers.Where(p => p == only).ToArray() : providers;
if (selected.Length == 0) throw new ArgumentException($"--only must be one of: {string.Join(", ", providers)}");

var scratch = $"dbdatasync_bench_{Guid.NewGuid():N}";
Console.WriteLine($"{rows:N0} rows x {columns} columns = {(long)rows * columns:N0} cells; median of {repeat} run(s) per provider");
Console.WriteLine($"{Environment.OSVersion}, {Environment.ProcessorCount} cores, .NET {Environment.Version}");
Console.WriteLine();

var needsMsSql = selected.Any(p => p.StartsWith("mssql", StringComparison.Ordinal));
var needsPostgres = selected.Any(p => p.StartsWith("postgres", StringComparison.Ordinal));
if (needsMsSql) await WithMasterAsync(new SqlConnection(new SqlConnectionStringBuilder(mssql) { InitialCatalog = "master" }.ConnectionString), $"CREATE DATABASE [{scratch}];");
if (needsPostgres) await WithMasterAsync(new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres) { Database = "postgres" }.ConnectionString), $"CREATE DATABASE {scratch};");

try
{
    Console.WriteLine(Measurement.Header + "     rows/s");
    Console.WriteLine(new string('-', Measurement.Header.Length + 11));
    foreach (var provider in selected)
    {
        var runs = new List<Measurement>();
        for (var i = 0; i < repeat; i++) runs.Add(await RunChildAsync(provider, scratch));
        var median = runs.OrderBy(m => m.ElapsedMs).ElementAt(runs.Count / 2);
        Console.WriteLine($"{median}  {rows / (median.ElapsedMs / 1000.0),10:N0}");
    }
}
finally
{
    if (needsMsSql)
        await WithMasterAsync(new SqlConnection(new SqlConnectionStringBuilder(mssql) { InitialCatalog = "master" }.ConnectionString),
            $"ALTER DATABASE [{scratch}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{scratch}];");
    if (needsPostgres)
    {
        NpgsqlConnection.ClearAllPools();
        await WithMasterAsync(new NpgsqlConnection(new NpgsqlConnectionStringBuilder(postgres) { Database = "postgres" }.ConnectionString), $"DROP DATABASE IF EXISTS {scratch} WITH (FORCE);");
    }
}
return 0;

async Task<Measurement> RunChildAsync(string provider, string database)
{
    var startInfo = new ProcessStartInfo { FileName = "dotnet", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    startInfo.ArgumentList.Add("exec");
    startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DbDataSync.Benchmarks.Staging.dll"));
    foreach (var arg in new[] { "--provider", provider, "--database", database, "--rows", rows.ToString(), "--columns", columns.ToString(), "--mssql", mssql, "--postgres", postgres })
        startInfo.ArgumentList.Add(arg);

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the benchmark child process.");
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException($"{provider} failed:\n{stderr}");
    return Measurement.FromWire(stdout.Trim());
}

static async IAsyncEnumerable<ChangeRow> Generate(ChangeSchema schema, int rows, int columns)
{
    for (var r = 0; r < rows; r++)
    {
        var values = new object?[columns];
        for (var c = 0; c < columns; c++)
            values[c] = BenchmarkSchema.KindOf(c) switch
            {
                ColumnKind.Int => BenchmarkSchema.IntValue(r, c),
                ColumnKind.Money => BenchmarkSchema.MoneyValue(r, c),
                ColumnKind.Timestamp => DateTime.SpecifyKind(BenchmarkSchema.TimestampValue(r), DateTimeKind.Unspecified),   // binary COPY refuses a UTC value for a timestamp without time zone
                _ => BenchmarkSchema.SharedText,
            };
        yield return new ChangeRow(ChangeOperation.Insert, schema, values);
    }
    await Task.CompletedTask;
}

static string SqlServerType(int c) => BenchmarkSchema.KindOf(c) switch
{
    ColumnKind.Int => "int", ColumnKind.Money => "decimal(18,2)", ColumnKind.Timestamp => "datetime2(3)", _ => "nvarchar(50)",
};

static string PostgresType(int c) => BenchmarkSchema.KindOf(c) switch
{
    ColumnKind.Int => "integer", ColumnKind.Money => "numeric(18,2)", ColumnKind.Timestamp => "timestamp(3) without time zone", _ => "character varying(50)",
};

static string CreateSqlServerTable(string table, int columns) =>
    $"CREATE TABLE dbo.[{table}] ({string.Join(", ", Enumerable.Range(0, columns).Select(c => $"[{BenchmarkSchema.NameOf(c)}] {SqlServerType(c)} NULL"))});";

static string CreatePostgresTable(string table, int columns) =>
    $"CREATE TABLE public.\"{table}\" ({string.Join(", ", Enumerable.Range(0, columns).Select(c => $"\"{BenchmarkSchema.NameOf(c)}\" {PostgresType(c)} NULL"))});";

static async Task ExecuteAsync(DbConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 300;
    await command.ExecuteNonQueryAsync();
}

static async Task WithMasterAsync(DbConnection connection, string sql)
{
    await using (connection)
    {
        await connection.OpenAsync();
        await ExecuteAsync(connection, sql);
    }
}
