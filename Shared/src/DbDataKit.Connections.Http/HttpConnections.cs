using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Http;

/// <summary>What a caller can give: a handler to send through instead of the platform's (a test's recorded answers), and a clock (a test's, to move past a token's expiry).</summary>
public sealed record HttpConnectionOptions(HttpMessageHandler? InnerHandler = null, TimeProvider? Time = null);

/// <summary>An <see cref="HttpClient"/> of a connection of kind <c>http</c>: its base address is the endpoint, every request signs in as the connection says, and the client's timeouts are the connection's.</summary>
public sealed class HttpConnectionClient : IDisposable
{
    private readonly OAuthTokenSource? _tokens;

    private readonly string[] _secrets;

    internal HttpConnectionClient(HttpClient client, OAuthTokenSource? tokens, X509Certificate2? certificate, IReadOnlyList<string>? secrets = null)
    {
        Client = client;
        _tokens = tokens;
        Certificate = certificate;
        _secrets = [.. (secrets ?? []).Where(s => s.Length > 0)];
    }

    /// <summary>
    /// The text with the composed address and each secret part of it (plain and escaped) replaced by <c>***</c>. An <see cref="HttpClient"/> failure can carry the request address; a caller that shows or logs the words of one passes them through here first
    /// (the Test result and the failure classes never carry them at all).
    /// </summary>
    public string Redact(string? text) => SecretRedaction.Redact(text, _secrets);

    public HttpClient Client { get; }

    /// <summary>How many times the token address of OAuth 2 was called by this client; 0 for another sign-in.</summary>
    public int TokenRequests => _tokens?.Fetches ?? 0;

    internal X509Certificate2? Certificate { get; }

    public void Dispose()
    {
        Client.Dispose();
        Certificate?.Dispose();
    }
}

/// <summary>
/// Makes the client of a connection of kind <c>http</c> (plan 27a IX02). Sign-in is one of: nothing, an API key (in a header or the query), basic, OAuth 2 client credentials (the token cached and refreshed once when it expires or the system refuses it), or a client certificate
/// (a PKCS#12 file or a secret that holds it as Base64). TLS is verified unless the connection opts out, and then only for that connection; <c>tls.ca_file</c> trusts that authority's certificates for this connection alone. The secrets are references, read when a request is sent.
/// </summary>
public static class HttpConnections
{
    public static HttpConnectionClient Create(ConnectionDefinition definition, ISecretResolver secrets, HttpConnectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.KindName != ConnectionKinds.Http || definition.Endpoint is null)
            throw new ArgumentException("The connection is not of kind http with an endpoint.", nameof(definition));
        options ??= new();
        var spec = definition.SignIn ?? new SignInSpec();
        var time = options.Time ?? TimeProvider.System;

        X509Certificate2? certificate = null;
        HttpMessageHandler inner;
        if (options.InnerHandler is { } given) inner = given;
        else
        {
            if (spec.Method == "client_certificate") certificate = LoadCertificate(spec, secrets);
            inner = Sockets(definition, certificate);
        }

        OAuthTokenSource? tokens = spec.Method == "oauth2_client_credentials" ? new OAuthTokenSource(spec, secrets, new HttpMessageInvoker(inner, disposeHandler: false), time) : null;
        var handler = new SignInHandler(spec, secrets, tokens, inner, time);
        var (composed, parts) = ComposeEndpoint(definition, secrets);
        // an API's address is a base for relative paths (a trailing slash); a webhook's is the whole address, which a slash would change
        var endpoint = composed.EndsWith('/') || composed.Contains('?', StringComparison.Ordinal) || definition.Webhook is not null ? composed : composed + "/";
        var client = new HttpClient(handler) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(definition.Timeouts.CommandS) };
        return new HttpConnectionClient(client, tokens, certificate, [composed, .. parts]);
    }

    /// <summary>The address of a connection with its placeholders filled from the secrets (or the whole address when it is a secret), and the secret values used: the address is secret, and exists only in the call.</summary>
    internal static (string Address, IReadOnlyList<string> Parts) ComposeEndpoint(ConnectionDefinition definition, ISecretResolver secrets)
    {
        var endpoint = definition.Endpoint!;
        if (EndpointTemplate.IsHidden(endpoint))
        {
            var whole = SecretText.ReadAsync(secrets, endpoint, default).GetAwaiter().GetResult().Trim();
            return (whole, [whole]);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in EndpointTemplate.Names(endpoint))
            values[name] = definition.EndpointSecrets.TryGetValue(name, out var reference) ? SecretText.ReadAsync(secrets, reference, default).GetAwaiter().GetResult().Trim() : throw new ArgumentException($"The placeholder {{{name}}} of the endpoint is not bound.", nameof(definition));
        return (EndpointTemplate.Compose(endpoint, n => values[n]), [.. values.Values]);
    }

    private static SocketsHttpHandler Sockets(ConnectionDefinition d, X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(d.Timeouts.ConnectS), PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false };
        if (certificate is not null) handler.SslOptions.ClientCertificates = [certificate];
        if (!d.Tls.EffectiveVerify) handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        else if (d.Tls.CaFile is { Length: > 0 } caFile)
        {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(caFile);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null) return false;
                if (errors == SslPolicyErrors.None) return true;
                if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(new X509Certificate2(cert));
            };
        }
        return handler;
    }

    private static X509Certificate2 LoadCertificate(SignInSpec spec, ISecretResolver secrets)
    {
        try
        {
            var password = spec.CertificatePasswordSecret is null ? null : SecretText.ReadAsync(secrets, spec.CertificatePasswordSecret, default).GetAwaiter().GetResult();
            if (spec.CertificateFile is { } file) return X509CertificateLoader.LoadPkcs12FromFile(file, password);
            var base64 = SecretText.ReadAsync(secrets, spec.CertificateSecret!, default).GetAwaiter().GetResult();
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64.Trim()), password);
        }
        catch (SecretNotSetException) { throw; }
        // what failed is told by its kind, never by the loader's own message (it can hold a path or part of the file)
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException or FormatException or UnauthorizedAccessException)
        {
            throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Tls, "client-certificate", HttpFailures.Engine));
        }
    }
}
