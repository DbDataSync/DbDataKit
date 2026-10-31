using DbDataKit.Odbc.Metadata;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// A Flight SQL login value that cannot be used. The message is ours and says what is wrong with the grammar; it never holds a part of the value (the value carries a token or a password).
/// </summary>
public sealed class FlightSqlLoginValueException(string message) : Exception(message);

/// <summary>
/// The value of a Flight SQL connection as a person or a product writes it in one string: a URI and options separated by <c>;</c>, written like an ODBC connection string (a value in braces may hold <c>;</c> and <c>=</c>, and <c>}}</c> is a <c>}</c>).
/// <code>grpc+tls://proxy.example.com:31337;token=eyJ...
/// grpc://127.0.0.1:31337;user=reader;password={p;w}
/// grpc+tls://proxy.example.com:31337;token_command={az account get-access-token --resource api://proxy --query accessToken -o tsv}</code>
/// The schemes are <c>grpc</c> and <c>grpc+tcp</c> (no TLS) and <c>grpc+tls</c>. The options are <c>token</c> (a bearer token), <c>token_env</c> (the name of an environment variable that holds one), <c>token_command</c> (a program a
/// client runs when it connects; it prints the token), <c>user</c> and <c>password</c> (the server's basic-authentication handshake, which trades them for a bearer token), <c>tls_skip_verify</c> (<c>true</c>: do not check the server's
/// certificate), <c>ca_file</c> (a PEM file of the certificate authority to trust instead of the machine's) and <c>header.&lt;name&gt;</c> (a header sent with every call). At most one way to authenticate; none means the server is called
/// without credentials. This type only reads the grammar: running a <c>token_command</c> or reading <c>token_env</c> is the caller's, and a server that must not do either (the Proxy) refuses those two options itself.
/// </summary>
public sealed record FlightSqlLoginValue(
    string Scheme, string Host, int Port, string? Token, string? TokenEnv, string? TokenCommand, string? User, string? Password, bool TlsSkipVerify, string? CaFile,
    IReadOnlyList<(string Name, string Value)> Headers)
{
    public bool Tls => Scheme == "grpc+tls";

    /// <summary><c>host:port</c>: what a screen shows of the login.</summary>
    public string HostAndPort => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>The address Grpc.Net.Client takes: the scheme is http or https, which is how it is told to use TLS.</summary>
    public Uri Address => new($"{(Tls ? "https" : "http")}://{HostAndPort}");

    /// <summary>How the login authenticates, in words (no secret): <c>bearer token</c>, <c>user bob</c>, <c>no credentials</c>.</summary>
    public string AuthenticationDescription => Token != null ? "bearer token" : TokenEnv != null ? "bearer token from the environment" : TokenCommand != null ? "bearer token from a command"
        : User != null ? $"user {User}" : "no credentials";

    /// <summary>True when the token is to come from a program or the environment of whoever connects.</summary>
    public bool TokenFromOutside => TokenEnv != null || TokenCommand != null;

    private static readonly string[] Options = ["token", "token_env", "token_command", "user", "password", "tls_skip_verify", "ca_file", "header.<name>"];

    private static FlightSqlLoginValueException Bad(string what) =>
        new($"The Flight SQL login {what}. It is grpc://host:port or grpc+tls://host:port, then ;option=value parts: {string.Join(", ", Options)}.");

    /// <summary>Reads the value. Throws <see cref="FlightSqlLoginValueException"/> (a sentence of ours, no part of the value in it) when it does not follow the grammar.</summary>
    public static FlightSqlLoginValue Parse(string value)
    {
        var text = value.Trim();
        var split = text.IndexOf(';');
        var uriText = (split < 0 ? text : text[..split]).Trim();
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw Bad("does not start with a URI");
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("grpc" or "grpc+tcp" or "grpc+tls")) throw Bad("has a scheme that is not grpc, grpc+tcp or grpc+tls");
        if (uri.Port <= 0) throw Bad("has no port");
        if (uri.AbsolutePath.Trim('/').Length > 0 || uri.Query.Length > 0 || uri.UserInfo.Length > 0) throw Bad("has a path, query or user in the URI (credentials are options)");
        string? token = null, tokenEnv = null, tokenCommand = null, user = null, password = null, caFile = null;
        var skip = false;
        var headers = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, v) in split < 0 ? [] : OdbcConnectionString.Parse(text[(split + 1)..]))
        {
            if (!seen.Add(key) && !key.StartsWith("header.", StringComparison.Ordinal)) throw Bad("repeats an option");
            switch (key)
            {
                case "token": token = v; break;
                case "token_env": tokenEnv = v; break;
                case "token_command": tokenCommand = v; break;
                case "user": user = v; break;
                case "password": password = v; break;
                case "ca_file": caFile = v; break;
                case "tls_skip_verify":
                    skip = v.Trim().ToLowerInvariant() switch { "true" or "1" or "yes" => true, "false" or "0" or "no" => false, _ => throw Bad("has a tls_skip_verify that is not true or false") };
                    break;
                default:
                    if (key.StartsWith("header.", StringComparison.Ordinal) && key.Length > 7) { headers.Add((key[7..], v)); break; }
                    throw Bad("has an option that is not one it knows");
            }
        }
        var methods = new[] { token != null, tokenEnv != null, tokenCommand != null, user != null || password != null }.Count(b => b);
        if (methods > 1) throw Bad("names more than one way to authenticate (token, token_env, token_command, or user and password)");
        if ((user == null) != (password == null)) throw Bad("has a user without a password, or a password without a user");
        if (token is { Length: 0 } || tokenEnv is { Length: 0 } || tokenCommand is { Length: 0 } || user is { Length: 0 }) throw Bad("has an empty credential option");
        if (!(scheme == "grpc+tls") && (skip || caFile != null)) throw Bad("sets a TLS option on a connection without TLS");
        if (skip && caFile != null) throw Bad("sets both tls_skip_verify and ca_file");
        return new(scheme, uri.DnsSafeHost, uri.Port, token, tokenEnv, tokenCommand, user, password, skip, caFile, headers);
    }

    /// <summary>The settings of a connection with this login, the token (or password) given: <paramref name="token"/> is the resolved bearer token, or null when the login has a user and password or none.</summary>
    public FlightSqlSettings ToSettings(string? token, TimeSpan connectTimeout, TimeSpan commandTimeout) =>
        new(Address, token != null ? FlightSqlAuthMethod.Bearer : User != null ? FlightSqlAuthMethod.Basic : FlightSqlAuthMethod.None,
            User, token ?? Password, TlsSkipVerify, Catalog: null, connectTimeout, commandTimeout, FlightSqlParameterMode.Prepared,
            CaFile: Tls && !TlsSkipVerify ? CaFile : null, Headers: Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList());
}
