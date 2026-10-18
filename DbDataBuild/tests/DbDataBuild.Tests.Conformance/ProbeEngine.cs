using System.Globalization;

namespace DbDataBuild.Tests.Conformance;

/// <summary>One row of the table every probe reads (id, i, j, d, n, s, dt, ts), as text: a value is written into each engine's own literal syntax.</summary>
public sealed record ProbeRow(int Id, string I, string J, string D, string N, string? S, string? Dt, string? Ts);

/// <summary>The rows an engine returned, as the driver gave them, with the engine's name for each column's type.</summary>
public sealed record ProbeRows(IReadOnlyList<object?[]> Rows, IReadOnlyList<string> TypeNames);

/// <summary>An engine rejected a statement. The message is the exception type and the first line of the driver's message.</summary>
public sealed class EngineQueryException(string message) : Exception(message);

/// <summary>
/// What the probes need from an engine, and nothing more: a table of the probe rows and a way to run a query. The engines that also run the whole tool (SQL Server, PostgreSQL) are <see cref="Engine"/>s; the
/// engines that are only checked for their dialect (Oracle, Spark SQL, the BigQuery emulator) implement this alone.
/// </summary>
public interface IProbeEngine : IAsyncDisposable
{
    /// <summary>The target and dialect name: sqlserver, postgres, oracle, spark or bigquery.</summary>
    string Name { get; }

    /// <summary>What the run is called in the report and the baselines: the target name, or one of its variants (a SQL Server 2025 at compatibility level 160).</summary>
    string Label => Name;

    /// <summary>The engine has no empty string: it stores `''` as NULL (Oracle). A case whose answers differ only on the row that holds `''` is then a documented class of difference, not an undiagnosed one.</summary>
    bool EmptyStringIsNull => false;

    /// <summary>The engine version the tool is told to generate for (`targets.sqlserver.version`), or null when none is configured.</summary>
    int? Version => null;

    Task StartAsync();

    Task CreateProbeTableAsync(IReadOnlyList<ProbeRow> rows);

    /// <exception cref="EngineQueryException">the engine rejected the statement</exception>
    Task<ProbeRows> QueryAsync(string sql);
}

/// <summary>Only loopback engines may be used.</summary>
public static class Loopback
{
    public static void Require(string host)
    {
        var allowed = new[] { "127.0.0.1", "localhost", "::1", "[::1]" };
        if (!allowed.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to run conformance tests against `{host}`: only local throwaway engines are allowed.");
    }
}
