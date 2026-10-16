using System.Data.Common;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataKit.Secrets;

namespace DbDataProxy.Data;

/// <summary>
/// One data source's connection as the shared Connections layer sees it: a provider plus a <see cref="ConnectionContext"/> (definition, the
/// administrator's connection string as a whole-string login, policy). The Proxy's own settings travel in <c>extensions.proxy</c> of the definition.
/// </summary>
public sealed class SourceConnection
{
    private SourceConnection(IDbProvider provider, ConnectionContext context)
    {
        Provider = provider;
        Context = context;
    }

    public IDbProvider Provider { get; }
    public ConnectionContext Context { get; }
    public ConnectionDefinition Definition => Context.Definition;

    /// <summary>"odbc", "sqlserver" or "duckdb".</summary>
    public string Engine => Provider.Engine.Value;

    public bool ReadOnly => Context.ReadOnly;

    public ParameterStyle ParameterStyle => Engine == "sqlserver" ? ParameterStyle.NamedAtSign : ParameterStyle.Positional;

    /// <summary>Marker scanning, quoting and statement classification for this engine.</summary>
    public DbDataKit.Dialect.ISqlDialect Dialect => ProviderDialects.For(Engine);

    /// <summary>
    /// Builds the definition for a data source. <paramref name="connectionString"/> is the administrator's string after secret resolution (and,
    /// for pass-through, after credential injection); its explicit TLS and timeout keys are carried into the definition so the composer keeps them.
    /// </summary>
    public static ConnectionDefinition BuildDefinition(
        string name, string engine, string connectionString, bool readOnly, int? commandTimeoutSeconds = null, YamlNode? proxyExtension = null)
    {
        var definition = new ConnectionDefinition
        {
            Name = name,
            Engine = engine,
            Access = readOnly ? AccessMode.Readonly : AccessMode.Readwrite,
            Tls = ConnectionHints.TlsFrom(engine, connectionString),
            Timeouts = ConnectionHints.TimeoutsFrom(engine, connectionString, commandTimeoutSeconds),
        };
        if (proxyExtension is not null) definition.Extensions.Items["proxy"] = proxyExtension;
        return definition;
    }

    public static SourceConnection Create(IDbProvider provider, ConnectionDefinition definition, string connectionString, ConnectionPolicy? policy = null)
    {
        // The string carries its own credentials (or integrated security), so it goes in as an administrator-authored whole-string login.
        var login = new ResolvedLogin(LoginRole.Default, AuthMode.Sql, "proxy:config") { ConnectionString = SecretValue.From(connectionString) };
        return new SourceConnection(provider, new ConnectionContext { Definition = definition, Login = login, Policy = policy ?? ConnectionPolicy.Default });
    }

    /// <summary>A source from just a name and string (tests, the worker): definition built with <see cref="BuildDefinition"/>.</summary>
    public static SourceConnection Create(IDbProvider provider, string name, string connectionString, bool readOnly = false, int? commandTimeoutSeconds = null) =>
        Create(provider, BuildDefinition(name, provider.Engine.Value, connectionString, readOnly, commandTimeoutSeconds), connectionString);

    /// <summary>Creates, opens and (for read-only sources) sandboxes a connection. Failures arrive as <see cref="ConnectionFailureException"/>.</summary>
    public Task<DbConnection> OpenAsync(CancellationToken cancellationToken) => Provider.OpenAsync(Context, cancellationToken);

    /// <summary>The composed connection string including credentials: a secret. Used where a driver API wants the string (ODBC catalog functions) and to validate the configuration.</summary>
    public string Compose() => Provider.Composer.Compose(Context);
}
