using System.Data.Common;
using System.Globalization;
using DbData.Dialect;
using DbData.Odbc.Metadata;
using DbData.Secrets;

namespace DbData.Connections;

/// <summary>An ordered key/value view of a connection string under construction. Implementations do the engine's own quoting.</summary>
public interface IKeyBag
{
    void Set(string key, string value);
    bool Remove(string key);
    bool TryGet(string key, out string value);
    IReadOnlyList<string> Keys { get; }
    string Render();
}

/// <summary>A bag over a (provider typed) <see cref="DbConnectionStringBuilder"/>: quoting and keyword synonyms are the builder's.</summary>
public sealed class BuilderBag(DbConnectionStringBuilder builder, Func<DbConnectionStringBuilder>? factory = null) : IKeyBag
{
    public DbConnectionStringBuilder Builder { get; private set; } = builder;
    public void Set(string key, string value) => Builder[key] = value;
    /// <summary>
    /// Removes a key and proves it is gone. Npgsql's <c>Remove</c> reports success but keeps some keywords in the string, so when the key
    /// is still there the builder is rebuilt without it; if it still cannot be removed this throws (fail closed).
    /// </summary>
    public bool Remove(string key)
    {
        bool removed = Builder.Remove(key);
        string norm = CredentialPolicy.NormalizeKey(key);
        bool Present() => new DbConnectionStringBuilder { ConnectionString = Builder.ConnectionString }.Keys.Cast<string>().Any(k => CredentialPolicy.NormalizeKey(k) == norm);
        if (!Present()) return removed;

        var keep = new List<KeyValuePair<string, string>>();
        var parsed = new DbConnectionStringBuilder { ConnectionString = Builder.ConnectionString };
        foreach (string k in parsed.Keys)
            if (CredentialPolicy.NormalizeKey(k) != norm) keep.Add(new(k, Convert.ToString(parsed[k], CultureInfo.InvariantCulture) ?? ""));
        var fresh = factory is null ? new DbConnectionStringBuilder() : factory();
        foreach (var (k, v) in keep) fresh[k] = v;
        Builder = fresh;
        if (Present()) throw new InvalidOperationException($"The key '{key}' could not be removed from the connection string.");
        return true;
    }
    public bool TryGet(string key, out string value)
    {
        if (Builder.ShouldSerialize(key) && Builder.TryGetValue(key, out var o) && o is not null) { value = Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""; return true; }
        value = ""; return false;
    }
    /// <summary>Only keys that are set: typed builders report every valid keyword from <c>Keys</c>, so the rendered string is read back.</summary>
    public IReadOnlyList<string> Keys => new DbConnectionStringBuilder { ConnectionString = Builder.ConnectionString }.Keys.Cast<string>().ToList();
    public string Render() => Builder.ConnectionString;
}

/// <summary>An ODBC string under construction. Values are written by <see cref="OdbcConnectionString.Quote"/>: brace quoting with <c>}}</c> doubling, never double quotes.</summary>
public sealed class OdbcBag : IKeyBag
{
    private readonly List<KeyValuePair<string, string>> _pairs = [];

    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["driver"] = "Driver", ["dsn"] = "DSN", ["uid"] = "UID", ["pwd"] = "PWD", ["server"] = "Server", ["database"] = "Database",
    };

    public static OdbcBag Parse(string text)
    {
        var bag = new OdbcBag();
        foreach (var (key, value) in OdbcConnectionString.Parse(OdbcConnectionString.Normalize(text)))
            bag.Set(Canonical.TryGetValue(key, out var c) ? c : key, value);
        return bag;
    }

    private int IndexOf(string key) => _pairs.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public void Set(string key, string value)
    {
        int i = IndexOf(key);
        if (i >= 0) _pairs[i] = new(_pairs[i].Key, value); else _pairs.Add(new(key, value));
    }

    public bool Remove(string key)
    {
        bool any = false;
        for (int i; (i = IndexOf(key)) >= 0;) { _pairs.RemoveAt(i); any = true; }
        return any;
    }

    public bool TryGet(string key, out string value)
    {
        int i = IndexOf(key);
        value = i >= 0 ? _pairs[i].Value : "";
        return i >= 0;
    }

    public IReadOnlyList<string> Keys => _pairs.Select(p => p.Key).ToList();
    public string Render() => OdbcConnectionString.Build(_pairs);
}

/// <summary>
/// The shared composition pipeline: base string, address, TLS, timeouts, properties, read-only keys, then credentials last.
/// Properties are checked against <see cref="CredentialPolicy"/> first, credentials replace whatever is left of their keys, and the
/// rendered string is parsed again to prove the credentials survived quoting exactly (the hostile-credential guarantee).
/// </summary>
public abstract class ComposerBase(EngineId engine) : IConnectionStringComposer
{
    public const int MaxPassThroughLength = 4096;

    public EngineId Engine { get; } = engine;

    /// <summary>An empty bag using the engine's quoting.</summary>
    protected abstract IKeyBag NewBag();
    /// <summary>Reads an existing string. Throws <see cref="ConnectionPolicyException"/> when it is malformed (the text is never included).</summary>
    protected abstract IKeyBag ParseText(string text);
    /// <summary>Key names this engine uses for the login.</summary>
    protected abstract string UserKey { get; }
    protected abstract string PasswordKey { get; }
    /// <summary>Marks the connection as integrated authentication (Windows or Kerberos).</summary>
    protected abstract void SetIntegrated(IKeyBag bag);
    /// <summary>False for engines with no login (DuckDB): a login that carries credentials is then refused, never silently ignored.</summary>
    protected virtual bool UsesCredentials => true;

    /// <summary>Fixed (descriptor) properties, applied before everything else.</summary>
    protected virtual void ApplyFixed(IKeyBag bag, ConnectionContext ctx) { }
    /// <summary>Throws when <paramref name="key"/> may not be set from <c>properties</c> for engine specific reasons.</summary>
    protected virtual void CheckProperty(string key) { }
    protected virtual void ApplyAddress(IKeyBag bag, AddressSpec address, ConnectionContext ctx) { }
    protected virtual void ApplyTls(IKeyBag bag, TlsSpec tls, ConnectionContext ctx) { }
    protected virtual void ApplyTimeouts(IKeyBag bag, TimeoutSpec timeouts, ConnectionContext ctx) { }
    /// <summary>Connection-string keys added when the connection is read-only.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> ReadOnlyKeys(IKeyBag bag, ConnectionContext ctx) => [];
    /// <summary>Called on every administrator or user authored base string before it is parsed (ODBC policy).</summary>
    protected virtual void CheckBaseText(string text, ConnectionContext? ctx) { }

    private static bool IsUserKey(string key) => CredentialPolicy.NormalizeKey(key) is "userid" or "uid" or "user" or "username" or "login" or "loginname";
    private bool IsSecretKey(string key) =>
        CredentialKeyDetector.IsCredentialKey(key) || string.Equals(CredentialPolicy.NormalizeKey(key), CredentialPolicy.NormalizeKey(PasswordKey), StringComparison.Ordinal);

    public string Compose(ConnectionContext context)
    {
        var bag = Build(context);
        string text = bag.Render();
        VerifyRoundTrip(text, context.Login);
        return text;
    }

    public string Describe(ConnectionContext context)
    {
        var bag = Build(context);
        foreach (var key in bag.Keys.ToList())
            if (IsSecretKey(key)) bag.Set(key, SecretValue.Mask);
        return bag.Render();
    }

    private IKeyBag Build(ConnectionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var d = ctx.Definition;
        if (!ctx.Policy.AllowTlsOptOut && (!d.Tls.Encrypt || !d.Tls.EffectiveVerify))
            throw new ConnectionPolicyException("TLS opt-out (encrypt off, verify off or trust_server_certificate) is not allowed by policy");
        CredentialPolicy.ValidateProperties(d.Properties);

        var login = ctx.Login;
        string? baseText = login?.ConnectionString is { IsEmpty: false } whole ? whole.RevealString() : d.ConnectionString;
        IKeyBag bag;
        if (baseText is null) bag = NewBag();
        else { RejectControl(baseText); CheckBaseText(baseText, ctx); bag = ParseText(baseText); }

        ApplyFixed(bag, ctx);
        if (d.Address is { } address) ApplyAddress(bag, address, ctx);
        ApplyTls(bag, d.Tls, ctx);
        ApplyTimeouts(bag, d.Timeouts, ctx);
        foreach (var (key, value) in d.Properties) { CheckProperty(key); bag.Set(key, value); }
        if (ctx.ReadOnly) foreach (var (key, value) in ReadOnlyKeys(bag, ctx)) bag.Set(key, value);
        ApplyCredentials(bag, login);
        return bag;
    }

    private void ApplyCredentials(IKeyBag bag, ResolvedLogin? login)
    {
        if (login is null || login.Mode == AuthMode.None) return;
        if (login.Mode == AuthMode.Sql && login.UserName is null && login.Password is null) return; // a whole-string login brings its own
        if (!UsesCredentials) throw new ConnectionPolicyException("this engine takes no credentials");
        StripCredentialKeys(bag);
        switch (login.Mode)
        {
            case AuthMode.Integrated:
                SetIntegrated(bag);
                break;
            case AuthMode.Sql:
                if (login.UserName is not null) { RejectControl(login.UserName); bag.Set(UserKey, login.UserName); }
                if (login.Password is not null)
                {
                    string pw = login.Password.RevealString();
                    RejectControl(pw);
                    bag.Set(PasswordKey, pw);
                }
                break;
        }
    }

    private void StripCredentialKeys(IKeyBag bag)
    {
        foreach (var key in bag.Keys.ToList())
            if (CredentialPolicy.IsIntegratedKey(key) || IsUserKey(key) || IsSecretKey(key)) bag.Remove(key);
    }

    private void VerifyRoundTrip(string text, ResolvedLogin? login)
    {
        if (login is not { Mode: AuthMode.Sql } || (login.UserName is null && login.Password is null)) return;
        var again = ParseText(text);
        if (login.UserName is not null && (!again.TryGet(UserKey, out var u) || u != login.UserName))
            throw new InvalidOperationException("The composed connection string does not carry the user name unchanged.");
        if (login.Password is not null && (!again.TryGet(PasswordKey, out var p) || p != login.Password.RevealString()))
            throw new InvalidOperationException("The composed connection string does not carry the password unchanged.");
    }

    public string ApplyPassThroughCredentials(string baseConnectionString, string user, SecretValue password, ConnectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(baseConnectionString);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(password);
        if (!UsesCredentials) throw new ConnectionPolicyException("this engine takes no credentials");
        policy ??= ConnectionPolicy.Default;
        string pw = password.RevealString();
        if (user.Length == 0) throw new ConnectionPolicyException("pass-through user name is empty");
        if (user.Length > policy.MaxCredentialLength || pw.Length > policy.MaxCredentialLength) throw new ConnectionPolicyException("pass-through credential is too long");
        if (baseConnectionString.Length > MaxPassThroughLength * 4) throw new ConnectionPolicyException("connection string is too long");
        RejectControl(user); RejectControl(pw);
        RejectControl(baseConnectionString);

        CheckBaseText(baseConnectionString, null);
        var bag = ParseText(baseConnectionString);
        StripCredentialKeys(bag);
        bag.Set(UserKey, user);
        bag.Set(PasswordKey, pw);
        AssertNoIntegrated(bag);

        string text = bag.Render();
        var again = ParseText(text);
        AssertNoIntegrated(again);
        if (!again.TryGet(UserKey, out var u2) || u2 != user || !again.TryGet(PasswordKey, out var p2) || p2 != pw)
            throw new InvalidOperationException("The pass-through credentials did not survive quoting unchanged.");
        return text;
    }

    private static void AssertNoIntegrated(IKeyBag bag)
    {
        foreach (var key in bag.Keys)
            if (CredentialPolicy.IsIntegratedKey(key))
                throw new InvalidOperationException($"An integrated-security key ('{key}') remained after pass-through injection.");
    }

    protected static void RejectControl(string value)
    {
        foreach (char c in value)
            if (char.IsControl(c)) throw new ConnectionPolicyException("a connection string or credential contains a control character");
    }
}

/// <summary>Composers that use a <see cref="DbConnectionStringBuilder"/> (SqlClient, Npgsql, generic ADO.NET).</summary>
public abstract class BuilderComposer(EngineId engine) : ComposerBase(engine)
{
    protected abstract DbConnectionStringBuilder NewBuilder();
    protected override IKeyBag NewBag() => new BuilderBag(NewBuilder(), NewBuilder);

    protected override IKeyBag ParseText(string text)
    {
        var b = NewBuilder();
        try { b.ConnectionString = text; }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            // the message of a builder exception can quote the offending text, so it is dropped
            throw new ConnectionPolicyException("the connection string is malformed or uses a keyword this engine does not know");
        }
        return new BuilderBag(b, NewBuilder);
    }
}
