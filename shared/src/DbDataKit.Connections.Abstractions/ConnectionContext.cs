using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>A login whose secrets are resolved. Dispose to wipe the secret values.</summary>
public sealed class ResolvedLogin(LoginRole role, AuthMode mode, string source) : IDisposable
{
    public LoginRole Role { get; } = role;
    public AuthMode Mode { get; } = mode;
    /// <summary>Where it came from, for audit (names only, e.g. <c>env:DBDATA_BUILD_WH_READ</c>).</summary>
    public string Source { get; } = source;
    public string? UserName { get; init; }
    public SecretValue? Password { get; init; }
    /// <summary>A whole connection string (Build style env logins). Treated as administrator-authored.</summary>
    public SecretValue? ConnectionString { get; init; }
    public SecretValue? Token { get; init; }

    public void Dispose()
    {
        Password?.Dispose();
        ConnectionString?.Dispose();
        Token?.Dispose();
    }

    public override string ToString() => $"login {Role} {Mode} from {Source}";
}

/// <summary>Org-level policy applied by composers. Defaults are the secure ones.</summary>
public sealed record ConnectionPolicy
{
    public static ConnectionPolicy Default { get; } = new();

    /// <summary>False refuses any definition that sets <c>tls.trust_server_certificate</c>, <c>tls.verify: false</c> or <c>tls.encrypt: false</c>.</summary>
    public bool AllowTlsOptOut { get; init; } = true;
    /// <summary>ODBC driver names allowed in user-authored strings. Null: any driver.</summary>
    public IReadOnlySet<string>? AllowedOdbcDrivers { get; init; }
    /// <summary>ODBC DSN names allowed. Null: any DSN (unless only drivers are allowed).</summary>
    public IReadOnlySet<string>? AllowedOdbcDsns { get; init; }
    /// <summary>Longest user name or password accepted by pass-through injection.</summary>
    public int MaxCredentialLength { get; init; } = 512;
}

/// <summary>Definition plus resolved login plus policy: all a provider needs to create an unopened connection.</summary>
public sealed class ConnectionContext
{
    public required ConnectionDefinition Definition { get; init; }
    public ResolvedLogin? Login { get; init; }
    public ConnectionPolicy Policy { get; init; } = ConnectionPolicy.Default;
    public LoginRole Role { get; init; } = LoginRole.Default;

    /// <summary>True when the definition is read-only or the login role is read.</summary>
    public bool ReadOnly => Definition.Access == AccessMode.Readonly || Role == LoginRole.Read;
}
