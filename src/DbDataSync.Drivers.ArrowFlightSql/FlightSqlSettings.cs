using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.ArrowFlightSql;

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
/// Everything a Flight SQL connection needs, read out of a <see cref="ConnectionConfig"/> and the
/// already-resolved credential. The driver-specific settings live in <see cref="ConnectionConfig.Properties"/>
/// (the persisted shape has no other place for them) under the keys below.
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

    public static FlightSqlSettings From(ConnectionConfig connection, string? credential)
    {
        var properties = connection.Properties;
        var useTls = !properties.TryGetValue(UseTlsKey, out var tls) || !IsFalse(tls);

        var auth = connection.AuthMode switch
        {
            AuthMode.SqlAuth => properties.TryGetValue(AuthMethodKey, out var method)
                && Enum.TryParse<FlightSqlAuthMethod>(method, ignoreCase: true, out var parsed)
                && parsed != FlightSqlAuthMethod.None
                    ? parsed
                    : FlightSqlAuthMethod.Basic,
            AuthMode.IntegratedAuth => throw new NotSupportedException(
                "Arrow Flight SQL has no integrated authentication. Choose SQL Auth (basic or bearer) or None."),
            _ => FlightSqlAuthMethod.None,
        };

        if (auth == FlightSqlAuthMethod.Basic && string.IsNullOrEmpty(connection.UserId))
            throw new InvalidOperationException("Basic authentication needs a user id.");
        if (auth != FlightSqlAuthMethod.None && string.IsNullOrEmpty(credential))
            throw new InvalidOperationException(
                auth == FlightSqlAuthMethod.Bearer
                    ? "Bearer authentication needs a token, and none is stored for this connection."
                    : "Basic authentication needs a password, and none is stored for this connection.");

        var mode = properties.TryGetValue(ParameterModeKey, out var pm)
            && Enum.TryParse<FlightSqlParameterMode>(pm, ignoreCase: true, out var parsedMode)
                ? parsedMode
                : FlightSqlParameterMode.Prepared;

        return new FlightSqlSettings(
            ResolveAddress(connection, useTls),
            auth,
            auth == FlightSqlAuthMethod.Basic ? connection.UserId : null,
            auth == FlightSqlAuthMethod.None ? null : credential,
            properties.TryGetValue(SkipCertificateValidationKey, out var skip) && IsTrue(skip),
            string.IsNullOrWhiteSpace(connection.Database) ? null : connection.Database,
            Seconds(ConnectionTimeouts.ResolveConnectSeconds(connection)),
            Seconds(ConnectionTimeouts.ResolveCommandSeconds(connection)),
            mode,
            Text(properties, DataSourceKey),
            Text(properties, CompressionKey));
    }

    /// <summary>
    /// Host and port, or a full URI. <c>grpc://</c> and <c>grpc+tcp://</c> are plain text and
    /// <c>grpc+tls://</c> is TLS — the spellings Flight SQL clients and servers print — alongside
    /// ordinary <c>http(s)://</c>. A bare <c>host:port</c> in the connection-string field takes its
    /// scheme from the TLS setting.
    /// </summary>
    internal static Uri ResolveAddress(ConnectionConfig connection, bool useTls)
    {
        var scheme = useTls ? "https" : "http";

        string text;
        if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
        {
            text = connection.ConnectionString.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(connection.Host))
        {
            text = connection.Port is { } port ? $"{connection.Host.Trim()}:{port}" : connection.Host.Trim();
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

    private static string? Text(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static TimeSpan Seconds(int seconds) => seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);

    private static bool IsTrue(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";

    private static bool IsFalse(string value) =>
        value.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";
}
