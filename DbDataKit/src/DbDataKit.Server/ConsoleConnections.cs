using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.Connections;
using DbDataKit.Connections.Files;
using DbDataKit.Connections.Http;
using DbDataKit.Knowledge;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;

namespace DbDataKit.Server;

/// <summary>A pasted webhook address as the editor keeps it: the service it was recognised as, the message format it takes (null: none of ours), the template for the file and the references of its secret parts (never the parts).</summary>
public sealed record WebhookSplit(string Service, string? Format, string Template, IReadOnlyDictionary<string, string> Secrets);

/// <summary>One secret reference of a connection: where it is written, the reference (never a value) and whether it can be read now.</summary>
public sealed record ConnectionSecretView(string Field, string Path, string Reference, bool Available, string? Problem, bool KeptHere = false);

/// <summary>An outside connection (kind <c>http</c>, <c>model-provider</c> or <c>smtp</c>) as the screen lists it: its file, what the loader says, its kind and address, how it signs in, and whether each secret it names can be read (never a secret).</summary>
public sealed record OutsideConnectionView(string Name, string File, string Yaml, string? Kind, string? Endpoint, string? SignIn, bool? Cloud, IReadOnlyList<ConnectionSecretView> Secrets, IReadOnlyList<DiagnosticView> Diagnostics, ConnectionHealth? Health = null);

/// <summary>Whether a connection that signs in with a person's consent (OAuth 2 authorization code, plan 27a IX03) can sign in now. <see cref="Ok"/> false is "unhealthy": the message says what a person does (give consent again). Never a secret.</summary>
public sealed record ConnectionHealth(bool Ok, string Message, DateTimeOffset At);

/// <summary>What a consent that came back (or did not) became: whether the refresh token is kept now, and a sentence for the person.</summary>
public sealed record ConsentOutcome(bool Ok, string? Connection, string Message);

/// <summary>
/// The connections of outside systems the console keeps (plan 27a IX01): <c>connections/&lt;name&gt;.yml</c> in the console's directory, <c>connection/2</c> files of kind <c>http</c> or <c>model-provider</c>, read with the shared strict loader. A file holds secret
/// <b>references</b>; a value a person types on the screen is kept in the console's secret store as <see cref="SecretNameFor"/> and is never in a file, a view, an audit row or a message. The model providers the agents use are the connections of kind <c>model-provider</c>
/// here (<see cref="ConsoleModels"/>).
/// </summary>
public sealed partial class ConsoleConnections
{
    /// <summary>The fields a typed secret can be kept under, with what each is called on the screen.</summary>
    public static readonly string[] SecretFields = ["key", "password", "client_secret", "certificate", "certificate_password", "private_key", "passphrase"];

    /// <summary>Whether <paramref name="field"/> can be kept as a typed secret: one of <see cref="SecretFields"/>, or the name of a placeholder of an endpoint template (<c>token</c>, <c>sig</c>, ...).</summary>
    public static bool IsSecretField(string field) => SecretFields.Contains(field, StringComparer.Ordinal) || PlaceholderName().IsMatch(field);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderName();

    /// <summary>Where a connection reaches, as the screens show it: the endpoint's template (or "address hidden"), never the composed address; a mail server as <c>host:port (tls)</c>.</summary>
    public static string? TargetOf(ConnectionDefinition d) =>
        d.FileServer is { } fs ? $"{d.KindName}://{fs.Host}:{fs.Port ?? (d.KindName == ConnectionKinds.Ftps ? (fs.FtpsMode == "implicit" ? 990 : 21) : 22)}{(fs.Root == "/" ? "" : fs.Root)}"
        : d.Smtp is { } smtp ? $"{smtp.Host}:{smtp.Port ?? (smtp.Tls switch { "implicit" => 465, "none" => 25, _ => 587 })} ({smtp.Tls})"
        : d.Endpoint is { } endpoint ? EndpointTemplate.Display(endpoint)
        : d.ModelProvider is { } m ? ModelProviderSpec.DefaultEndpoint(m.Provider) : null;

    private readonly string _directory;
    private readonly EncryptedFileSecretStore _store;
    private readonly ISecretResolver _secrets;
    private readonly HttpConnectionOptions _http;
    private readonly TimeProvider _time;
    private readonly string _healthFile;
    private readonly object _healthGate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingConsent> _pending = new();

    /// <summary>A consent that was started and has not come back: which connection, the PKCE verifier and the address the provider returns to (the token request names it again).</summary>
    private sealed record PendingConsent(string Connection, string Verifier, string RedirectUri, DateTimeOffset Expires);

    /// <summary>How long a person has between pressing Authorize and coming back from the provider.</summary>
    public static readonly TimeSpan ConsentWindow = TimeSpan.FromMinutes(10);

    /// <summary>Writes a rotated refresh token to the console's secret store (a reference that is not the store's cannot be written to).</summary>
    private sealed class RefreshTokenWriter(ConsoleConnections owner) : IRefreshTokenStore
    {
        public async Task SaveAsync(string reference, string refreshToken, CancellationToken ct)
        {
            var parsed = SecretRef.Parse(reference);
            if (parsed.Kind != SecretRefKind.Secret) throw new InvalidOperationException("Only a secret kept by the console can be written.");
            using var value = SecretValue.From(refreshToken);
            await owner._store.SetAsync(SecretName.Parse(parsed.Target), value, ct).ConfigureAwait(false);
        }
    }

    public ConsoleConnections(ConsoleSettings settings, EncryptedFileSecretStore store, HttpConnectionOptions? http = null)
    {
        _time = http?.Time ?? TimeProvider.System;
        _healthFile = Path.Combine(settings.Directory, "connection-health.json");
        _http = (http ?? new HttpConnectionOptions()) with { RefreshTokens = new RefreshTokenWriter(this), ConsentLost = (name, message) => SetHealth(name, false, message), TokenRefreshed = name => SetHealth(name, true, "Signed in with the stored consent.") };
        _directory = settings.Directory;
        _store = store;
        _secrets = new SecretResolverChain([store, new EnvSecretProvider(), new FileSecretProvider()]);   // `file:` references work for what a connection names (a webhook's address, a key kept by the system)
    }

    public string Folder => Path.Combine(_directory, "connections");

    /// <summary>Raised after a file is saved or removed and after a secret is kept: what caches the providers (<see cref="ConsoleModels"/>) starts again.</summary>
    public event Action? Changed;

    public ISecretResolver Secrets => _secrets;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsName(string name) => NamePattern().IsMatch(name);

    /// <summary>Where a person's typed secret of a connection is kept.</summary>
    public static SecretName SecretNameFor(string connection, string field) => new("server", "connection", connection, field);

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
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml.TrimStart('﻿'), $"connections/{name}.yml", addressRequired: false);
        var diagnostics = result.Diagnostics.ToList();
        if (result.Definition is { } d)
        {
            if (!d.IsOutside)
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ConnectionDefinitionLoader.BadKind, $"connections/{name}.yml", 1, 1, "The console keeps connections of outside systems here (kind `http`, `model-provider`, `smtp`, `sftp`, `scp` or `ftps`); a database connection is kept by the server that uses it.", "Add `kind: http`, or keep the database connection on its Sync or Gateway server."));
            else if (d.Name != name)
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ConnectionDefinitionLoader.BadOutside, $"connections/{name}.yml", 1, 1, $"The file is named {name}.yml but the connection is `{d.Name}`.", "A connection's file is named after it."));
        }
        var ok = !diagnostics.HasErrors();
        return new ConnectionLoadResult(ok ? result.Definition : null, diagnostics);
    }

    /// <summary>The references a definition holds, as (field, path in the file, reference).</summary>
    public static IEnumerable<(string Field, string Path, string Reference)> ReferencesOf(ConnectionDefinition d)
    {
        if (d.Endpoint is { } endpoint && EndpointTemplate.IsHidden(endpoint)) yield return ("endpoint", "endpoint", endpoint);
        foreach (var (part, reference) in d.EndpointSecrets) yield return (part, "endpoint_secrets." + part, reference);
        if (d.SignIn is not { } s) yield break;
        if (s.KeySecret is not null) yield return ("key", "sign_in.key_secret", s.KeySecret);
        if (s.PasswordSecret is not null) yield return ("password", "sign_in.password_secret", s.PasswordSecret);
        if (s.ClientSecretSecret is not null) yield return ("client_secret", "sign_in.client_secret_secret", s.ClientSecretSecret);
        if (s.CertificateSecret is not null) yield return ("certificate", "sign_in.certificate_secret", s.CertificateSecret);
        if (s.CertificatePasswordSecret is not null) yield return ("certificate_password", "sign_in.certificate_password_secret", s.CertificatePasswordSecret);
        if (s.RefreshTokenSecret is not null) yield return ("refresh_token", "sign_in.refresh_token_secret", s.RefreshTokenSecret);
        if (s.PrivateKeySecret is not null) yield return ("private_key", "sign_in.private_key_secret", s.PrivateKeySecret);
        if (s.PassphraseSecret is not null) yield return ("passphrase", "sign_in.passphrase_secret", s.PassphraseSecret);
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
        var secrets = d is null ? [] : ReferencesOf(d).Select(r => { var (ok, problem) = Probe(r.Reference); return new ConnectionSecretView(r.Field, r.Path, r.Reference, ok, problem, r.Reference == ReferenceFor(name, r.Field)); }).ToList();
        var endpoint = d is null ? null : TargetOf(d);          // the template, never the composed address
        return new OutsideConnectionView(name, $"connections/{name}.yml", yaml, d?.Kind, endpoint, d is null ? null : d.SignIn?.Method ?? "none", d?.ModelProvider is { } p ? (p.Locality ?? (p.Provider == "ollama" ? "local" : "cloud")) == "cloud" : null, secrets, [.. result.Diagnostics.Select(DiagnosticView.Of)], d?.SignIn?.Method == "oauth2_authorization_code" ? HealthOf(name) : null);
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

    /// <summary>
    /// <b>Test</b> of a connection (plan 27a IX02): for an <c>http</c> connection it signs in as the file says and calls the endpoint once (<see cref="HttpConnectionTester"/>), and says whether the system was reached and whether it accepted the sign-in; for a model provider the
    /// answer says to test a model on the Models screen. Null when there is no such connection that reads. Nothing in the answer is a secret.
    /// </summary>
    public async Task<HttpTestResult?> TestAsync(string name, CancellationToken cancellation = default)
    {
        if (!IsName(name) || !File.Exists(FileOf(name))) return null;
        var read = Check(name, File.ReadAllText(FileOf(name)));
        if (read.Definition is not { } d) return new HttpTestResult(false, null, "not checked", $"The file of `{name}` has errors; fix them first ({read.Diagnostics.Count(x => x.Severity == ConfigSeverity.Error)} to see on the editor).", null, false, null, 0);
        if (d.KindName == ConnectionKinds.ModelProvider) return new HttpTestResult(false, null, "not checked", $"`{name}` is a model provider: test one of its models on the Models screen of the Agents page.", null, false, null, 0);
        if (d.KindName == ConnectionKinds.Smtp || d.Webhook is not null)
            return new HttpTestResult(false, null, "not checked", $"`{name}` is {(d.Webhook is not null ? "a webhook" : "a mail server")} that delivery channels send through: bind it to a channel on the Delivery page (Channels) and press Test there, which sends a message clearly marked as a test through it.", null, false, null, 0);
        if (ConnectionKinds.IsFileServer(d.KindName))
        {
            var files = await FileStoreTester.TestAsync(d, _secrets, cancellation).ConfigureAwait(false);
            return new HttpTestResult(files.Ok, files.Reached, files.SignIn, files.Message, files.Kind, files.Temporary, null, files.Milliseconds);
        }

        var result = await HttpConnectionTester.TestAsync(d, _secrets, _http, cancellation).ConfigureAwait(false);
        if (d.SignIn?.Method == "oauth2_authorization_code") SetHealth(name, result.Ok, result.Message);
        return result;
    }

    // ---- consent (plan 27a IX03) ----

    /// <summary>The definition of an authorization-code connection, or null when there is no such connection that reads.</summary>
    private ConnectionDefinition? ConsentDefinition(string name) =>
        IsName(name) && File.Exists(FileOf(name)) && Check(name, File.ReadAllText(FileOf(name))).Definition is { SignIn.Method: "oauth2_authorization_code" } d ? d : null;

    /// <summary>
    /// Starts the consent of an <c>oauth2_authorization_code</c> connection: the address of the provider's consent page, with a random state and a PKCE challenge, and the verifier kept here until the person comes back to <paramref name="redirectUri"/> (the console's own callback).
    /// Null when the connection does not sign in that way. A request that is not completed in <see cref="ConsentWindow"/> is forgotten.
    /// </summary>
    public string? BeginConsent(string name, string redirectUri)
    {
        if (ConsentDefinition(name) is not { } d) return null;
        var now = _time.GetUtcNow();
        foreach (var (key, value) in _pending) if (value.Expires < now) _pending.TryRemove(key, out _);
        var request = OAuthAuthorizationCode.Begin(d.SignIn!, redirectUri);
        _pending[request.State] = new PendingConsent(name, request.CodeVerifier, redirectUri, now + ConsentWindow);
        return request.Url;
    }

    /// <summary>
    /// The provider sent the person back (the callback): the state must be one this console issued and not used before, the code is exchanged for tokens, and the refresh token is kept as the connection's secret (<c>refresh_token</c>). Every refusal says in words what to do; nothing
    /// the provider sent back is echoed except its error name (letters, digits, '_', '-', '.', cut short).
    /// </summary>
    public async Task<ConsentOutcome> CompleteConsentAsync(string? state, string? code, string? error, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(state) || !_pending.TryRemove(state, out var pending)) return new ConsentOutcome(false, null, "This sign-in was not started here, or it was used already. Press Authorize again.");
        if (pending.Expires < _time.GetUtcNow()) return new ConsentOutcome(false, pending.Connection, "The sign-in took too long and was forgotten. Press Authorize again.");
        if (!string.IsNullOrEmpty(error))
        {
            var shown = new string([.. error.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.').Take(40)]);
            SetHealth(pending.Connection, false, $"The provider did not give consent ({shown}).");
            return new ConsentOutcome(false, pending.Connection, $"The provider did not give consent ({shown}). Press Authorize to try again.");
        }

        if (string.IsNullOrEmpty(code)) return new ConsentOutcome(false, pending.Connection, "The provider returned no code. Press Authorize to try again.");
        if (ConsentDefinition(pending.Connection) is not { } d) return new ConsentOutcome(false, pending.Connection, "The connection no longer signs in with consent.");
        try
        {
            using var invoker = HttpConnections.TokenInvoker(d, _http);
            var grant = await OAuthAuthorizationCode.ExchangeAsync(d.SignIn!, _secrets, invoker, code, pending.RedirectUri, pending.Verifier, cancellation).ConfigureAwait(false);
            if (grant.RefreshToken is not { } refresh)
            {
                SetHealth(pending.Connection, false, "The provider gave no refresh token.");
                return new ConsentOutcome(false, pending.Connection, "The provider gave no refresh token, so the connection could not sign in later. Ask for offline access in `sign_in.scope` (often `offline_access`) and press Authorize again.");
            }

            var reference = d.SignIn!.RefreshTokenSecret!;
            if (reference != ReferenceFor(pending.Connection, "refresh_token"))
                return new ConsentOutcome(false, pending.Connection, $"The refresh token would be kept as `{reference}`, which this console does not write. Name `{ReferenceFor(pending.Connection, "refresh_token")}` in `sign_in.refresh_token_secret`.");
            await StoreSecretAsync(pending.Connection, "refresh_token", refresh, cancellation).ConfigureAwait(false);
            SetHealth(pending.Connection, true, "Consent was given; the refresh token is kept in the console's secret store.");
            return new ConsentOutcome(true, pending.Connection, "Consent was given. The connection can sign in now.");
        }
        catch (ConnectionFailureException e)
        {
            var words = e.Failure.Code == "oauth-grant-revoked" ? "The provider did not accept the code (used already, or expired). Press Authorize again." : $"The provider refused the exchange: {ConnectionFailure.TextFor(e.Failure.Kind)} ({e.Failure.Code}). Check the client id and its secret.";
            SetHealth(pending.Connection, false, words);
            return new ConsentOutcome(false, pending.Connection, words);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            var kind = HttpFailures.Classify(e, cancellation).Kind;
            return new ConsentOutcome(false, pending.Connection, $"The token address could not be reached: {ConnectionFailure.TextFor(kind)} Press Authorize to try again.");
        }
    }

    /// <summary>
    /// Takes consent back: asks the provider to revoke the refresh token when the connection names a <c>revoke_url</c>, and forgets the token here whatever the provider answers (a provider that is down must not keep a token the person wants gone). Null when the connection does not
    /// sign in with consent. The outcome says which of the two happened.
    /// </summary>
    public async Task<ConsentOutcome?> RevokeConsentAsync(string name, CancellationToken cancellation = default)
    {
        if (ConsentDefinition(name) is not { } d) return null;
        var spec = d.SignIn!;
        var atProvider = "The connection has no `revoke_url`: the token was forgotten here only.";
        string? token = null;
        try { using var held = await _secrets.ResolveAsync(SecretRef.Parse(spec.RefreshTokenSecret!), cancellation).ConfigureAwait(false); token = held.RevealString(); }
        catch (SecretNotFoundException) { atProvider = "There was no token to revoke."; }
        if (token is not null && spec.RevokeUrl is not null)
        {
            try
            {
                using var invoker = HttpConnections.TokenInvoker(d, _http);
                await OAuthAuthorizationCode.RevokeAsync(spec, _secrets, invoker, token, cancellation).ConfigureAwait(false);
                atProvider = "The provider was told to revoke the token.";
            }
            catch (Exception e) when (e is ConnectionFailureException or HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
                { atProvider = "The provider could not be asked to revoke the token (it may still hold it; revoke it in the provider's own settings). The token was forgotten here."; }
        }

        if (spec.RefreshTokenSecret == ReferenceFor(name, "refresh_token")) await _store.DeleteAsync(SecretNameFor(name, "refresh_token"), cancellation).ConfigureAwait(false);
        Changed?.Invoke();
        ClearHealth(name);
        return new ConsentOutcome(true, name, atProvider);
    }

    // ---- health of a connection that signs in with consent ----

    public ConnectionHealth? HealthOf(string name)
    {
        lock (_healthGate) return ReadHealth().GetValueOrDefault(name);
    }

    private void SetHealth(string name, bool ok, string message)
    {
        lock (_healthGate)
        {
            var all = ReadHealth();
            all[name] = new ConnectionHealth(ok, message, _time.GetUtcNow());
            WriteHealth(all);
        }
    }

    private void ClearHealth(string name)
    {
        lock (_healthGate) { var all = ReadHealth(); if (all.Remove(name)) WriteHealth(all); }
    }

    private Dictionary<string, ConnectionHealth> ReadHealth()
    {
        try { return File.Exists(_healthFile) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ConnectionHealth>>(File.ReadAllText(_healthFile)) ?? [] : []; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return []; }
    }

    private void WriteHealth(Dictionary<string, ConnectionHealth> all)
    {
        try { File.WriteAllText(_healthFile, System.Text.Json.JsonSerializer.Serialize(all)); }
        catch (IOException) { /* health is a hint: a read-only directory must not stop a sign-in */ }
    }

    /// <summary>
    /// Splits a pasted webhook address into its visible template and its secret parts, keeps each part in the secret store under the connection's name (the typed-secret route's store, so a rotation changes only the secret) and returns the template and the references to write
    /// in the file: never the parts. A known service (<see cref="WebhookPatterns"/>) is split by its pattern; for another, <paramref name="pathSegments"/> (indexes of the path's segments) and <paramref name="queryKeys"/> say which parts are secret. Null when nothing is recognised and nothing is chosen.
    /// </summary>
    public async Task<WebhookSplit?> StoreWebhookAsync(string name, string url, IReadOnlyList<int>? pathSegments = null, IReadOnlyList<string>? queryKeys = null, CancellationToken cancellation = default)
    {
        WebhookDetection? found = pathSegments is { Count: > 0 } || queryKeys is { Count: > 0 } ? Generic(url.Trim(), pathSegments ?? [], queryKeys ?? []) : WebhookPatterns.Detect(url);
        if (found is null) return null;
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (part, value) in found.Parts) references[part] = await StoreSecretAsync(name, part, value, cancellation).ConfigureAwait(false);
        return new WebhookSplit(found.Service, found.Format, found.Template, references);
    }

    private static WebhookDetection? Generic(string url, IReadOnlyList<int> pathSegments, IReadOnlyList<string> queryKeys)
    {
        var m = Regex.Match(url, @"^(https?://[^/?#]+)(/[^?#]*)?(\?[^#]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var segments = (m.Groups[2].Success ? m.Groups[2].Value : "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in pathSegments.Distinct())
            if (i >= 0 && i < segments.Length) { parts["seg" + (i + 1)] = Uri.UnescapeDataString(segments[i]); segments[i] = "{seg" + (i + 1) + "}"; }
        var path = segments.Length == 0 ? "" : "/" + string.Join('/', segments);
        var query = new List<string>();
        foreach (var pair in (m.Groups[3].Success ? m.Groups[3].Value[1..] : "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            if (eq >= 0 && queryKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                var part = Regex.Replace(key, "[^A-Za-z0-9_]", "_");
                part = part.Length == 0 || !char.IsAsciiLetter(part[0]) ? "q" + part : part;
                parts[part] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                query.Add(key + "={" + part + "}");
            }
            else query.Add(pair);
        }

        return parts.Count == 0 ? null : new WebhookDetection("Other service", null, m.Groups[1].Value + path + (query.Count > 0 ? "?" + string.Join('&', query) : ""), parts);
    }

    /// <summary>A file to start from, per kind: references only, a value is typed on the screen and kept in the store. <paramref name="flavor"/> picks a webhook (<c>slack</c>, <c>teams</c>, <c>signed</c>) for kind <c>http</c>.</summary>
    public static string Template(string kind, string name, string? flavor = null) => kind switch
    {
        ConnectionKinds.Sftp => $"schema: connection/2\nkind: sftp\nname: {name}\nfile_server:\n  host: files.example.com\n  root: /upload\n  # pin the server: press Test once with `tls.verify: false`, confirm the fingerprint it shows with the server's administrator, write it here and remove `tls`\n  host_keys: []\ntls:\n  verify: false\nsign_in:\n  method: ssh_key\n  user: robot\n  private_key_secret: {ReferenceFor(name, "private_key")}\n  # passphrase_secret: {ReferenceFor(name, "passphrase")}\n",
        ConnectionKinds.Scp => $"schema: connection/2\nkind: scp\nname: {name}\nfile_server:\n  host: files.example.com\n  root: /home/robot\n  host_keys: []\ntls:\n  verify: false\nsign_in:\n  method: basic\n  user: robot\n  password_secret: {ReferenceFor(name, "password")}\n",
        ConnectionKinds.Ftps => $"schema: connection/2\nkind: ftps\nname: {name}\nfile_server:\n  host: files.example.com\n  ftps_mode: explicit\n  # certificate_sha256: [\"<64 hex digits: press Test, confirm with the server's administrator>\"]\nsign_in:\n  method: basic\n  user: robot\n  password_secret: {ReferenceFor(name, "password")}\n",
        ConnectionKinds.Smtp => $"schema: connection/2\nkind: smtp\nname: {name}\nsmtp:\n  host: smtp.example.com\n  tls: starttls\n  sender: \"DbData <noreply@example.com>\"\nsign_in:\n  method: basic\n  user: robot\n  password_secret: {ReferenceFor(name, "password")}\n",
        ConnectionKinds.Http when flavor == "slack" => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://hooks.slack.com/services/{{token}}\nendpoint_secrets:\n  token: {ReferenceFor(name, "token")}\nwebhook:\n  format: slack\n",
        ConnectionKinds.Http when flavor == "teams" => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://prod-00.westus.logic.azure.com:443/workflows/{{flow}}/triggers/manual/paths/invoke?api-version=2016-06-01&sig={{sig}}\nendpoint_secrets:\n  flow: {ReferenceFor(name, "flow")}\n  sig: {ReferenceFor(name, "sig")}\nwebhook:\n  format: teams\n",
        ConnectionKinds.Http when flavor == "signed" => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://receiver.example.com/hooks/{{id}}\nendpoint_secrets:\n  id: {ReferenceFor(name, "id")}\nwebhook:\n  format: signed\n  # allow_private: [receiver.internal.example, 10.1.0.0/16]\nsign_in:\n  method: signed_webhook\n  key_secret: {ReferenceFor(name, "key")}\n",
        ConnectionKinds.Http when flavor == "oauth" => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://api.example.com/v1\nsign_in:\n  method: oauth2_authorization_code\n  authorize_url: https://login.example.com/oauth2/authorize\n  token_url: https://login.example.com/oauth2/token\n  # revoke_url: https://login.example.com/oauth2/revoke\n  client_id: my-client-id\n  client_secret_secret: {ReferenceFor(name, "client_secret")}\n  scope: offline_access\n  refresh_token_secret: {ReferenceFor(name, "refresh_token")}\n",
        ConnectionKinds.Http => $"schema: connection/2\nkind: http\nname: {name}\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  name: X-Api-Key\n  key_secret: {ReferenceFor(name, "key")}\n",
        ConnectionKinds.ModelProvider => $"schema: connection/2\nkind: model-provider\nname: {name}\nsign_in:\n  method: api_key\n  key_secret: {ReferenceFor(name, "key")}\nmodel_provider:\n  provider: anthropic\n  models:\n    - {{ id: claude-sonnet-4-5, window: 200000, max_output: 8192, price: {{ input: 3, output: 15 }} }}\n",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The console keeps connections of kind http, model-provider, smtp, sftp, scp and ftps."),
    };
}
