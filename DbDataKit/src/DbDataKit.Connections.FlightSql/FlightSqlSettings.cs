namespace DbDataKit.Connections.FlightSql;

public enum FlightSqlAuthMethod
{
    /// <summary>No credentials are sent — an open server, or one that authenticates by address or network.</summary>
    None,

    /// <summary>User id and password, exchanged for a bearer token by the Flight handshake.</summary>
    Basic,

    /// <summary>A pre-issued token, sent as <c>Authorization: Bearer</c>.</summary>
    Bearer,
}

/// <summary>How the values of a command's parameters reach the server.</summary>
public enum FlightSqlParameterMode
{
    /// <summary>Prepared statement with positional <c>?</c> markers and bound Arrow values. The default:
    /// values never become part of the SQL text.</summary>
    Prepared,

    /// <summary>Values rendered into the SQL text as literals by the connection's dialect. For a server
    /// or proxy that does not implement prepared statements.</summary>
    Inline,
}

/// <summary>
/// Everything a Flight SQL connection needs. A product reads it out of its own connection settings, with the already-resolved credential; the property keys it may use are the constants below.
/// </summary>
public sealed record FlightSqlSettings(
    Uri Address,
    FlightSqlAuthMethod Auth,
    string? UserId,
    string? Credential,
    bool SkipCertificateValidation,
    string? Catalog,
    TimeSpan ConnectTimeout,
    TimeSpan CommandTimeout,
    FlightSqlParameterMode ParameterMode,
    string? DataSource = null,
    string? Compression = null)
{
    public const string AuthMethodKey = "authMethod";
    public const string UseTlsKey = "useTls";
    public const string SkipCertificateValidationKey = "skipCertificateValidation";
    public const string ParameterModeKey = "parameterMode";
    public const string DataSourceKey = "dataSource";
    public const string CompressionKey = "compression";

    /// <summary>Call header a proxy reads to choose which of its data sources runs the statement.</summary>
    public const string DataSourceHeader = "x-datasource";

    /// <summary>Call header a proxy reads to choose the codec for this call's results.</summary>
    public const string CompressionHeader = "x-compression";

    /// <summary>
    /// Host and port, or a full URI. <c>grpc://</c> and <c>grpc+tcp://</c> are plain text and
    /// <c>grpc+tls://</c> is TLS — the spellings Flight SQL clients and servers print — alongside
    /// ordinary <c>http(s)://</c>. A bare <c>host:port</c> in the connection-string field takes its
    /// scheme from the TLS setting.
    /// </summary>
    public static Uri ResolveAddress(string? connectionString, string? host, int? port, bool useTls)
    {
        var scheme = useTls ? "https" : "http";

        string text;
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            text = connectionString.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(host))
        {
            text = port is { } p ? $"{host.Trim()}:{p}" : host.Trim();
        }
        else
        {
            throw new InvalidOperationException("An Arrow Flight SQL connection needs a host, or an address.");
        }

        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
            return new Uri($"{scheme}://{text}");

        var given = text[..separator].ToLowerInvariant();
        var rest = text[(separator + 3)..];
        return given switch
        {
            "grpc" or "grpc+tcp" => new Uri($"http://{rest}"),
            "grpc+tls" => new Uri($"https://{rest}"),
            "http" or "https" => new Uri(text),
            _ => throw new InvalidOperationException(
                $"'{given}://' is not an address scheme Flight SQL uses. Expected grpc://, grpc+tcp://, grpc+tls://, http:// or https://."),
        };
    }

    public static string? Text(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    public static TimeSpan Seconds(int seconds) => seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);

    public static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    public static bool IsFalse(string value) =>
        value.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";
}
