using System.Data.Common;
using System.Globalization;
using DbData.Dialect;
using DbData.Odbc.Metadata;

namespace DbData.Connections;

/// <summary>Key spellings of one ADO.NET provider (descriptor <c>connection_string_keys</c>). Null keys are not written.</summary>
public sealed record GenericKeys
{
    public string Host { get; init; } = "Host";
    public string? Port { get; init; } = "Port";
    public string Database { get; init; } = "Database";
    public string Username { get; init; } = "User Id";
    public string Password { get; init; } = "Password";
    public string? ConnectTimeout { get; init; } = "Connect Timeout";
    public string? IntegratedSecurity { get; init; }
    /// <summary>Key and values for encryption, when the provider has one: (key, onValue, offValue).</summary>
    public (string Key, string On, string Off)? Encrypt { get; init; }
    /// <summary>Key and values for certificate validation: (key, verifyValue, trustValue).</summary>
    public (string Key, string Verify, string Trust)? Verify { get; init; }
    public string? CommandTimeout { get; init; }
}

public sealed record GenericComposerOptions
{
    public GenericKeys Keys { get; init; } = new();
    /// <summary>Write the string the ODBC way (braces, <c>}}</c> doubling) instead of the ADO.NET builder's double quotes (Sync 9.8).</summary>
    public bool OdbcQuoting { get; init; }
    /// <summary>Keys written first, overridable only through <see cref="OverridableFixed"/> (descriptor <c>fixed_properties</c>).</summary>
    public IReadOnlyDictionary<string, string> FixedProperties { get; init; } = new Dictionary<string, string>();
    public IReadOnlySet<string> OverridableFixed { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, string> ReadOnlyKeys { get; init; } = new Dictionary<string, string>();
    public string? DefaultDatabase { get; init; }
}

/// <summary>Composer for any ADO.NET provider using a plain <see cref="DbConnectionStringBuilder"/> and configurable key names.</summary>
public class GenericAdoComposer(EngineId engine, GenericComposerOptions? options = null) : ComposerBase(engine)
{
    protected GenericComposerOptions Options { get; } = options ?? new();
    protected GenericKeys Keys => Options.Keys;

    protected override string UserKey => Keys.Username;
    protected override string PasswordKey => Keys.Password;

    protected override IKeyBag NewBag() => Options.OdbcQuoting ? new OdbcBag() : new BuilderBag(new DbConnectionStringBuilder());

    protected override IKeyBag ParseText(string text)
    {
        if (Options.OdbcQuoting)
        {
            if (!OdbcConnectionString.TryParseStrict(OdbcConnectionString.Normalize(text), out _, out _))
                throw new ConnectionPolicyException("the connection string is malformed");
            return OdbcBag.Parse(text);
        }
        try { return new BuilderBag(new DbConnectionStringBuilder { ConnectionString = text }); }
        catch (ArgumentException) { throw new ConnectionPolicyException("the connection string is malformed"); }
    }

    private bool IsFixedLocked(string key) =>
        Options.FixedProperties.Keys.Any(k => string.Equals(CredentialPolicy.NormalizeKey(k), CredentialPolicy.NormalizeKey(key), StringComparison.Ordinal))
        && !Options.OverridableFixed.Any(k => string.Equals(CredentialPolicy.NormalizeKey(k), CredentialPolicy.NormalizeKey(key), StringComparison.Ordinal));

    /// <summary>Fixed values go in first. An overridable one only fills a gap; a locked one replaces whatever the base string said.</summary>
    protected override void ApplyFixed(IKeyBag bag, ConnectionContext ctx)
    {
        foreach (var (k, v) in Options.FixedProperties)
        {
            if (IsFixedLocked(k) || !bag.TryGet(k, out _)) bag.Set(k, v);
        }
    }

    protected override void CheckProperty(string key)
    {
        if (IsFixedLocked(key)) throw new ConnectionPolicyException("property is fixed by the engine descriptor and not marked overridable", key);
    }

    protected override void SetIntegrated(IKeyBag bag)
    {
        if (Keys.IntegratedSecurity is { } key) bag.Set(key, Options.OdbcQuoting ? "yes" : "true");
        else throw new ConnectionPolicyException("this engine has no integrated authentication key");
    }

    protected override void ApplyAddress(IKeyBag bag, AddressSpec a, ConnectionContext ctx)
    {
        if (a.Host is not null) bag.Set(Keys.Host, a.Host);
        if (a.Port is int port && Keys.Port is not null) bag.Set(Keys.Port, port.ToString(CultureInfo.InvariantCulture));
        var db = a.Database ?? Options.DefaultDatabase;
        if (db is not null) bag.Set(Keys.Database, db);
    }

    protected override void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx)
    {
        if (Keys.Encrypt is { } e) bag.Set(e.Key, tls.Encrypt ? e.On : e.Off);
        if (Keys.Verify is { } v) bag.Set(v.Key, tls.EffectiveVerify ? v.Verify : v.Trust);
    }

    protected override void ApplyTimeouts(IKeyBag bag, TimeoutSpec t, ConnectionContext ctx)
    {
        if (Keys.ConnectTimeout is { } k) bag.Set(k, t.ConnectS.ToString(CultureInfo.InvariantCulture));
        if (Keys.CommandTimeout is { } c) bag.Set(c, t.CommandS.ToString(CultureInfo.InvariantCulture));
    }

    protected override IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) => Options.ReadOnlyKeys;
}
