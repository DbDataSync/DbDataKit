namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// What the Flight SQL module reads from its host: compression (one setting for the server and an override per source), whether statement text may be logged, the call header that names the source, and how the server calls itself in <c>GetSqlInfo</c>.
/// Settings are read through functions so a host that reloads its configuration changes them for the next call without restarting the module.
/// </summary>
public sealed class FlightSqlOptions
{
    /// <summary>The call header that chooses the data source a statement runs against; without it the caller's default source is used.</summary>
    public const string DataSourceHeader = "x-datasource";

    public Func<CompressionOptions> Compression { get; init; } = () => new CompressionOptions();

    /// <summary>The compression a source asks for in place of the server's, or null.</summary>
    public Func<string, CompressionOptions?> SourceCompression { get; init; } = _ => null;

    /// <summary>Statement text appears in the log only when this says so; otherwise the log says it is hidden.</summary>
    public Func<bool> LogSql { get; init; } = () => false;

    /// <summary>Reported as the server's name when no source is chosen.</summary>
    public string ServerName { get; init; } = "DbDataKit";

    public string ServerVersion { get; init; } = typeof(FlightSqlOptions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
