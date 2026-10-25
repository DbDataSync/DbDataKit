using System.Globalization;
using System.Text;
using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// A data source as a file of the shared connection format, <c>connection/1</c> (plan 13, E02): <c>connections/&lt;name&gt;.yaml</c>, the same file a Sync or Build connection is, with what only the Proxy has under
/// <c>extensions.proxy</c>. It is read with the shared strict loader (unknown keys, no plaintext credential, secret references that parse, a login that is complete) and then <em>mapped</em> onto the settings the proxy has always run on,
/// so the strict validation of those settings, and everything that consumes them, is unchanged:
/// <list type="bullet">
/// <item><c>engine</c> is the provider (<c>sqlserver</c>, <c>odbc</c>, <c>duckdb</c>).</item>
/// <item>The connection string is <c>connection_string</c>, or the one made of <c>address</c> (SQL Server only); TLS opt-outs and the login are added to it: the user, and the password as the secret reference it is (<c>Password=env:X</c>), which the proxy resolves when it connects.
/// A data source that can write uses the write login, one that cannot the read login; a read login is never replaced by the write one.</item>
/// <item><c>connection_string_secret</c> is the whole connection string as a secret reference.</item>
/// <item><c>timeouts.command_s</c> is the command timeout when the file says it; <c>access: readonly</c> and <c>allow_writes</c> together are refused.</item>
/// <item><c>extensions.proxy</c> holds the rest (<c>pass_through</c>, <c>bitness</c>, <c>classifier</c>, <c>allow_writes</c>, <c>metadata</c>, <c>allowed_directories</c>, the limits, <c>batch_size</c>, <c>compression</c>), with snake_case keys.</item>
/// </list>
/// A file in the data source format of earlier releases (no <c>schema:</c>) is refused with a message naming it.
/// </summary>
internal static class ProxyConnectionFile
{
    private static readonly string[] Engines = ["sqlserver", "odbc", "duckdb"];

    /// <summary>Reads one file. Adds its diagnostics (errors and warnings) to <paramref name="diags"/>; on success calls <paramref name="add"/> for every flattened setting with the node it came from.</summary>
    public static void Read(string path, string name, string text, List<ConfigDiagnostic> diags, Action<string, string?, YamlNode?> add)
    {
        if (LooksLikeAnEarlierRelease(text))
        {
            diags.Add(ProxyConfig.Catalog.Create(SharedCodes.InvalidValue, path, 1, 1,
                $"'{name}.yaml' is a data source in the format of an earlier release (it has no `schema: connection/1`).",
                "Write it as a connection/1 file: schema, name, engine, connection_string, auth (user and password_secret), and the proxy's own settings under extensions.proxy. config.example/ beside the executable has one of each."));
            return;
        }

        var loaded = ConnectionDefinitionLoader.Load(text, path);
        diags.AddRange(loaded.Diagnostics);
        if (!loaded.Ok) return;
        var definition = loaded.Definition!;

        var root = (YamlMapping)StrictYamlReader.Read(text, path, new List<ConfigDiagnostic>())!;
        YamlNode? At(params string[] keys)
        {
            YamlNode? node = root;
            foreach (var key in keys) node = (node as YamlMapping)?.Get(key);
            return node;
        }
        void Error(string message, string? fix, YamlNode? at) =>
            diags.Add(ProxyConfig.Catalog.Create(SharedCodes.InvalidValue, path, at?.Line ?? 1, at?.Column ?? 1, message, fix));

        if (!string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
            Error($"`name` is `{definition.Name}`, and the file is called `{name}.yaml`; the file name is the name of the data source.", $"Name the file `{definition.Name}.yaml`, or set `name: {name}`.", At("name"));
        if (!Engines.Contains(definition.Engine, StringComparer.OrdinalIgnoreCase))
            Error($"`engine` is `{definition.Engine}`; the proxy serves {string.Join(", ", Engines)}.", null, At("engine"));
        if (diags.HasErrors()) return;

        var proxy = At("extensions", "proxy") as YamlMapping;
        var allowWrites = proxy?.Get("allow_writes") is YamlScalar { Value: var w } && bool.TryParse(w, out var writes) && writes;
        if (definition.Access == AccessMode.Readonly && allowWrites)
        {
            Error("The connection is `access: readonly` and `extensions.proxy.allow_writes` is true.", "Remove one of them.", proxy!.Get("allow_writes"));
            return;
        }

        var prefix = $"Proxy:DataSources:{name}";
        add($"{prefix}:Provider", definition.Engine.ToLowerInvariant(), At("engine"));
        var composed = Compose(definition, writes: allowWrites, Error, At);
        if (composed is null) return;
        add($"{prefix}:ConnectionString", composed, At("connection_string") ?? At("connection_string_secret") ?? At("address") ?? At("auth") ?? At("engine"));
        if (At("timeouts", "command_s") is YamlScalar timeout) add($"{prefix}:CommandTimeoutSeconds", timeout.Value, timeout);
        if (proxy is not null)
            foreach (var entry in proxy.Entries) FlattenExtension(entry.Value, $"{prefix}:{Pascal(entry.Key.Value)}", add);
    }

    /// <summary>The file has the keys of the earlier format and none of the new one.</summary>
    private static bool LooksLikeAnEarlierRelease(string text)
    {
        var diags = new List<ConfigDiagnostic>();
        return StrictYamlReader.Read(text, "x", diags) is YamlMapping map && !diags.HasErrors() && map.Get("schema") is null
            && (map.Get("provider") is not null || map.Get("connectionString") is not null);
    }

    private static string? Compose(ConnectionDefinition d, bool writes, Action<string, string?, YamlNode?> error, Func<string[], YamlNode?> at)
    {
        var engine = d.Engine.ToLowerInvariant();
        var login = d.LoginFor(writes ? LoginRole.Write : LoginRole.Read);

        // the whole string, as a secret reference
        if (d.ConnectionStringSecret is not null) return d.ConnectionStringSecret;
        if (login?.ConnectionStringSecret is not null) return login.ConnectionStringSecret;

        var parts = new List<string>();
        if (d.ConnectionString is not null) parts.Add(d.ConnectionString.Trim().TrimEnd(';'));
        else if (d.Address is { } address)
        {
            if (engine != "sqlserver")
            {
                error($"`address` makes a SQL Server connection string; a `{engine}` data source needs `connection_string`.", null, at(["address"]));
                return null;
            }
            if (address.Host is not null) parts.Add($"Server={address.Host}{(address.Port is { } port ? "," + port.ToString(CultureInfo.InvariantCulture) : "")}");
            if (address.Database is not null) parts.Add($"Database={address.Database}");
        }

        if (engine == "sqlserver")
        {
            // encrypt and verify are the defaults; only an opt-out is written
            if (!d.Tls.Encrypt) parts.Add("Encrypt=False");
            if (!d.Tls.EffectiveVerify) parts.Add("TrustServerCertificate=True");
        }

        switch (login?.Mode)
        {
            case AuthMode.Sql:
                parts.Add(engine == "odbc" ? $"UID={login.User}" : $"User Id={login.User}");
                parts.Add(engine == "odbc" ? $"PWD={login.PasswordSecret}" : $"Password={login.PasswordSecret}");
                break;
            case AuthMode.Integrated:
                parts.Add(engine == "odbc" ? "Trusted_Connection=Yes" : "Integrated Security=True");
                break;
            case AuthMode.Token:
                error("A `token` login is not one the proxy connects with.", "Use a `sql` or `integrated` login.", at(["auth"]));
                return null;
        }
        return string.Join(';', parts);
    }

    private static void FlattenExtension(YamlNode node, string prefix, Action<string, string?, YamlNode?> add)
    {
        switch (node)
        {
            case YamlScalar scalar:
                add(prefix, !scalar.Quoted && scalar.Value.Length == 0 ? null : scalar.Value, scalar);
                break;
            case YamlSequence sequence:
                for (var i = 0; i < sequence.Items.Count; i++) FlattenExtension(sequence.Items[i], $"{prefix}:{i}", add);
                break;
            case YamlMapping mapping:
                foreach (var entry in mapping.Entries) FlattenExtension(entry.Value, $"{prefix}:{Pascal(entry.Key.Value)}", add);
                break;
        }
    }

    /// <summary><c>allowed_directories</c> to <c>AllowedDirectories</c>: the keys of the settings the proxy binds are PascalCase.</summary>
    internal static string Pascal(string snake)
    {
        var sb = new StringBuilder(snake.Length);
        var up = true;
        foreach (var c in snake)
        {
            if (c == '_') { up = true; continue; }
            sb.Append(up ? char.ToUpperInvariant(c) : c);
            up = false;
        }
        return sb.ToString();
    }
}
