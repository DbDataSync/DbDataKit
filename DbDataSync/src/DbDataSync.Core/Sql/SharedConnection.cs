using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataSync.Core.Config;
using KitAuth = DbDataKit.Connections.AuthMode;
using SyncAuth = DbDataSync.Core.Config.AuthMode;

namespace DbDataSync.Core.Sql;

/// <summary>What one engine's driver tells <see cref="SharedConnection"/> about its connection-string dialect.</summary>
public sealed class SharedConnectionOptions
{
    /// <summary>The engine's composer from <c>DbDataKit.Connections.*</c>.</summary>
    public required IConnectionStringComposer Composer { get; init; }

    /// <summary>The database to use in host mode when the connection names none.</summary>
    public string? DefaultDatabase { get; init; }

    /// <summary>The spellings of the connect timeout the operator's own string may use; one of them in `properties` wins over the setting (as it always did), and one in the string is kept unless the setting says otherwise.</summary>
    public required string[] ConnectTimeoutKeys { get; init; }

    /// <summary>
    /// Keys that mean TLS for this engine, in any spelling. They may appear in the operator's connection string or in <c>properties</c>;
    /// the shared policy refuses them as properties, so they are read here into the definition's TLS section instead and removed from the properties.
    /// </summary>
    public required string[] TlsKeys { get; init; }

    /// <summary>Turns the TLS keys the operator wrote (name to value, <c>properties</c> over the connection string) into the definition's TLS section. The default section is Mandatory and verified.</summary>
    public required Action<IReadOnlyDictionary<string, string>, TlsSpec> ReadTls { get; init; }

    /// <summary>Properties this driver needs unless the operator's string or <c>properties</c> already name the key (SQL Server's MARS).</summary>
    public IReadOnlyDictionary<string, string> DefaultProperties { get; init; } = new Dictionary<string, string>();

    /// <summary>The synonyms to look for before adding a default property, by its key.</summary>
    public IReadOnlyDictionary<string, string[]> DefaultPropertySynonyms { get; init; } = new Dictionary<string, string[]>();

    /// <summary>True when integrated authentication still needs a user name written (PostgreSQL: Kerberos and peer authentication name a role).</summary>
    public bool IntegratedNeedsUser { get; init; }
}

/// <summary>
/// Builds a Sync connection's string through the shared composers (plan 13, A08): the same TLS default, credential escaping and round-trip proof
/// Proxy and Build get. Sync's own rules stay here: an operator's connection string is the base, its own connect timeout is kept, and the
/// TLS keys it carries are honoured rather than overwritten.
/// </summary>
public static class SharedConnection
{
    public static string Compose(ConnectionConfig connection, string? credential, SharedConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        bool hasString = !string.IsNullOrWhiteSpace(connection.ConnectionString);
        var supplied = new DbConnectionStringBuilder();
        if (hasString) supplied.ConnectionString = connection.ConnectionString;

        var definition = new ConnectionDefinition
        {
            Name = connection.Name,
            Engine = options.Composer.Engine.Value,
            ConnectionString = hasString ? connection.ConnectionString : null,
            Address = new AddressSpec
            {
                Host = hasString ? null : connection.Host,
                Port = connection.Port,
                Database = !string.IsNullOrWhiteSpace(connection.Database) ? connection.Database : hasString ? null : options.DefaultDatabase,
            },
        };

        // TLS: what the operator wrote, in the string or in properties (properties win, as they always did).
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in options.TlsKeys)
            if (supplied.TryGetValue(key, out var v) && v is not null) written[Norm(key)] = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        foreach (var (key, value) in connection.Properties)
            if (IsTlsKey(options, key)) written[Norm(key)] = value;
        options.ReadTls(written, definition.Tls);

        // Timeouts: the setting wins; a connect timeout already in the operator's string is kept; otherwise the default.
        definition.Timeouts.CommandS = ConnectionTimeouts.ResolveCommandSeconds(connection);
        var propertyTimeout = connection.Properties.FirstOrDefault(p => IsConnectTimeoutKey(options, p.Key));
        if (propertyTimeout.Key is not null)
            definition.Timeouts.ConnectS = int.TryParse(propertyTimeout.Value, out var fromProperty)
                ? fromProperty
                : throw new InvalidOperationException($"The property '{propertyTimeout.Key}' must be a whole number of seconds.");
        else if (connection.ConnectTimeoutSeconds is int connect) definition.Timeouts.ConnectS = connect;
        else if (hasString && options.ConnectTimeoutKeys.FirstOrDefault(supplied.ContainsKey) is { } own
                 && int.TryParse(Convert.ToString(supplied[own], System.Globalization.CultureInfo.InvariantCulture), out var kept))
            definition.Timeouts.ConnectS = kept;
        else definition.Timeouts.ConnectS = ConnectionTimeouts.DefaultConnectSeconds;

        foreach (var (key, value) in connection.Properties)
            if (!IsTlsKey(options, key) && !IsConnectTimeoutKey(options, key)) definition.Properties[key] = value;
        foreach (var (key, value) in options.DefaultProperties)
        {
            string[] names = [key, .. options.DefaultPropertySynonyms.GetValueOrDefault(key) ?? []];
            if (names.Any(supplied.ContainsKey) || definition.Properties.Keys.Any(k => names.Any(n => Norm(n) == Norm(k)))) continue;
            definition.Properties[key] = value;
        }

        using var login = LoginFor(connection, credential, options);
        return options.Composer.Compose(new ConnectionContext { Definition = definition, Login = login });
    }

    private static ResolvedLogin? LoginFor(ConnectionConfig connection, string? credential, SharedConnectionOptions options)
    {
        switch (connection.AuthMode)
        {
            case SyncAuth.IntegratedAuth when options.IntegratedNeedsUser:
                // no password: the composer writes the role name and leaves the rest to Kerberos or peer authentication
                return new ResolvedLogin(LoginRole.Default, KitAuth.Sql, "sync")
                {
                    UserName = connection.UserId ?? throw new InvalidOperationException("UserId is required even for IntegratedAuth on PostgreSQL."),
                };
            case SyncAuth.IntegratedAuth:
                return new ResolvedLogin(LoginRole.Default, KitAuth.Integrated, "sync");
            case SyncAuth.SqlAuth:
                return new ResolvedLogin(LoginRole.Default, KitAuth.Sql, "sync")
                {
                    UserName = connection.UserId ?? throw new InvalidOperationException("UserId is required for SqlAuth connections."),
                    Password = SecretValue.From(credential ?? throw new InvalidOperationException("A resolved credential is required for SqlAuth connections.")),
                };
            default:
                // AuthMode.None: whatever the address or the environment provides is used, and DbDataSync adds nothing.
                return null;
        }
    }

    private static string Norm(string key) => CredentialPolicy.NormalizeKey(key);

    private static bool IsConnectTimeoutKey(SharedConnectionOptions options, string key) =>
        options.ConnectTimeoutKeys.Any(t => Norm(t) == Norm(key));

    private static bool IsTlsKey(SharedConnectionOptions options, string key) =>
        options.TlsKeys.Any(t => Norm(t) == Norm(key));
}
