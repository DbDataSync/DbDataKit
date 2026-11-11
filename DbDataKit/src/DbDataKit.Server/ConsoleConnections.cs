using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataKit.Knowledge;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataKit.Server;

/// <summary>One secret reference of a connection: where it is written, the reference (never a value) and whether it can be read now.</summary>
public sealed record ConnectionSecretView(string Field, string Path, string Reference, bool Available, string? Problem);

/// <summary>An outside connection (kind <c>http</c> or <c>model-provider</c>) as the screen lists it: its file, what the loader says, its kind and address, how it signs in, and whether each secret it names can be read (never a secret).</summary>
public sealed record OutsideConnectionView(string Name, string File, string Yaml, string? Kind, string? Endpoint, string? SignIn, bool? Cloud, IReadOnlyList<ConnectionSecretView> Secrets, IReadOnlyList<DiagnosticView> Diagnostics);

/// <summary>
/// The connections of outside systems the console keeps (plan 27a IX01): <c>connections/&lt;name&gt;.yml</c> in the console's directory, <c>connection/2</c> files of kind <c>http</c> or <c>model-provider</c>, read with the shared strict loader. A file holds secret
/// <b>references</b>; a value a person types on the screen is kept in the console's secret store as <see cref="SecretNameFor"/> and is never in a file, a view, an audit row or a message. The model providers the agents use are the connections of kind <c>model-provider</c>
/// here (<see cref="ConsoleModels"/>).
/// </summary>
public sealed partial class ConsoleConnections
{
    /// <summary>The fields a typed secret can be kept under, with what each is called on the screen.</summary>
    public static readonly string[] SecretFields = ["key", "password", "client_secret", "certificate", "certificate_password"];

    private readonly string _directory;
    private readonly EncryptedFileSecretStore _store;
    private readonly ISecretResolver _secrets;

    public ConsoleConnections(ConsoleSettings settings, EncryptedFileSecretStore store)
    {
        _directory = settings.Directory;
        _store = store;
        _secrets = new SecretResolverChain([store, new EnvSecretProvider()]);
    }

    public string Folder => Path.Combine(_directory, "connections");

    /// <summary>Raised after a file is saved or removed and after a secret is kept: what caches the providers (<see cref="ConsoleModels"/>) starts again.</summary>
    public event Action? Changed;

    public ISecretResolver Secrets => _secrets;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsName(string name) => NamePattern().IsMatch(name);

    /// <summary>Where a person's typed secret of a connection is kept.</summary>
    public static SecretName SecretNameFor(string connection, string field) => new("console", "connection", connection, field);

    /// <summary>The reference to write in a connection's file for the secret kept here.</summary>
    public static string ReferenceFor(string connection, string field) => "secret:" + SecretNameFor(connection, field);

    public string FileOf(string name) => Path.Combine(Folder, name + ".yml");

    /// <summary>A stamp that changes when a file does: what readers key a cache by.</summary>
    public string Stamp() =>
        !Directory.Exists(Folder) ? "" : string.Join('|', Directory.EnumerateFiles(Folder, "*.yml").Order(StringComparer.Ordinal).Select(f => $"{Path.GetFileName(f)}:{File.GetLastWriteTimeUtc(f).Ticks}:{new FileInfo(f).Length}"));

    // ---- reading ----

    /// <summary>Every file, read; a file that does not read is listed with its diagnostics and has no definition.</summary>
    public IReadOnlyList<(string Name, string Yaml, ConnectionLoadResult Result)> Read()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<(string, string, ConnectionLoadResult)>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.yml").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var yaml = File.ReadAllText(file);
            list.Add((name, yaml, Check(name, yaml)));
        }
        return list;
    }

    /// <summary>The definition of <paramref name="name"/> when its file reads; null when there is none or it does not.</summary>
    public ConnectionDefinition? Find(string name) =>
        IsName(name) && File.Exists(FileOf(name)) && Check(name, File.ReadAllText(FileOf(name))) is { Ok: true } r ? r.Definition : null;

    /// <summary>The loader's answer for a file's text, plus the rule that a file is named after its connection.</summary>
    public static ConnectionLoadResult Check(string name, string yaml)
    {
        var result = ConnectionDefinitionLoader.Load(yaml.TrimStart('﻿'), $"connections/{name}.yml", addressRequired: false, outsideKinds: true);
        var diagnostics = result.Diagnostics.ToList();
        if (result.Definition is { } d)
        {
            if (!d.IsOutside)
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ConnectionDefinitionLoader.BadKind, $"connections/{name}.yml", 1, 1, "The console keeps connections of outside systems here (kind `http` or `model-provider`); a database connection is kept by the server that uses it.", "Add `kind: http`, or keep the database connection on its Sync or Gateway server."));
            else if (d.Name != name)
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ConnectionDefinitionLoader.BadOutside, $"connections/{name}.yml", 1, 1, $"The file is named {name}.yml but the connection is `{d.Name}`.", "A connection's file is named after it."));
        }
        var ok = !diagnostics.HasErrors();
        return new ConnectionLoadResult(ok ? result.Definition : null, diagnostics);
    }

    /// <summary>The references a definition holds, as (field, path in the file, reference).</summary>
    public static IEnumerable<(string Field, string Path, string Reference)> ReferencesOf(ConnectionDefinition d)
    {
        if (d.SignIn is not { } s) yield break;
        if (s.KeySecret is not null) yield return ("key", "sign_in.key_secret", s.KeySecret);
        if (s.PasswordSecret is not null) yield return ("password", "sign_in.password_secret", s.PasswordSecret);
        if (s.ClientSecretSecret is not null) yield return ("client_secret", "sign_in.client_secret_secret", s.ClientSecretSecret);
        if (s.CertificateSecret is not null) yield return ("certificate", "sign_in.certificate_secret", s.CertificateSecret);
        if (s.CertificatePasswordSecret is not null) yield return ("certificate_password", "sign_in.certificate_password_secret", s.CertificatePasswordSecret);
    }

    /// <summary>Whether a reference can be read now, without keeping what it reads; a problem in words when it cannot.</summary>
    public (bool Available, string? Problem) Probe(string reference)
    {
        try
        {
            using var value = _secrets.ResolveAsync(SecretRef.Parse(reference)).AsTask().GetAwaiter().GetResult();
            return (true, null);
        }
        catch (SecretNotFoundException) { return (false, $"The secret `{reference}` is not set. Type it here (it is kept in the console's secret store), or set the secret or variable that names."); }
        catch (Exception e) when (e is not OperationCanceledException) { return (false, $"The secret `{reference}` could not be read."); }
    }

    // ---- the screen ----

    public IReadOnlyList<OutsideConnectionView> List() => [.. Read().Select(r => ViewOf(r.Name, r.Yaml, r.Result))];

    public OutsideConnectionView ViewOf(string name, string yaml, ConnectionLoadResult result)
    {
        var d = result.Definition;
        var secrets = d is null ? [] : ReferencesOf(d).Select(r => { var (ok, problem) = Probe(r.Reference); return new ConnectionSecretView(r.Field, r.Path, r.Reference, ok, problem); }).ToList();
        var endpoint = d?.Endpoint ?? (d?.ModelProvider is { } m ? ModelProviderSpec.DefaultEndpoint(m.Provider) : null);
        return new OutsideConnectionView(name, $"connections/{name}.yml", yaml, d?.Kind, endpoint, d is null ? null : d.SignIn?.Method ?? "none", d?.ModelProvider is { } p ? (p.Locality ?? (p.Provider == "ollama" ? "local" : "cloud")) == "cloud" : null, secrets, [.. result.Diagnostics.Select(DiagnosticView.Of)]);
    }

    // ---- writing ----

    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics) Save(string name, string yaml)
    {
        var result = Check(name, yaml);
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Diagnostics.HasErrors()) return (false, diagnostics);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(name), ConnectionDefinitionLoader.WithCurrentVersion(yaml));
        Changed?.Invoke();
        return (true, diagnostics);
    }

    public bool Delete(string name)
    {
        var file = FileOf(name);
        if (!IsName(name) || !File.Exists(file)) return false;
        File.Delete(file);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Keeps a typed secret of a connection in the console's store. Returns the reference to write in the file.</summary>
    public async Task<string> StoreSecretAsync(string name, string field, string value, CancellationToken cancellation = default)
    {
        using var secret = SecretValue.From(value);
        await _store.SetAsync(SecretNameFor(name, field), secret, cancellation).ConfigureAwait(false);
        Changed?.Invoke();
        return ReferenceFor(name, field);
    }

    /// <summary>A file to start from, per kind: references only, a value is typed on the screen and kept in the store.</summary>
    public static string Template(string kind, string name) => kind switch
    {
        ConnectionKinds.Http => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  name: X-Api-Key\n  key_secret: {ReferenceFor(name, "key")}\n",
        ConnectionKinds.ModelProvider => $"schema: connection/2\nkind: model-provider\nname: {name}\nsign_in:\n  method: api_key\n  key_secret: {ReferenceFor(name, "key")}\nmodel_provider:\n  provider: anthropic\n  models:\n    - {{ id: claude-sonnet-4-5, window: 200000, max_output: 8192, price: {{ input: 3, output: 15 }} }}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The console keeps connections of kind http and model-provider."),
    };
}
