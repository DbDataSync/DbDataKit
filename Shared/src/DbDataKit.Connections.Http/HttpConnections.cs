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

    internal HttpConnectionClient(HttpClient client, OAuthTokenSource? tokens, X509Certificate2? certificate)
    {
        Client = client;
        _tokens = tokens;
        Certificate = certificate;
    }

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
            throw new ArgumentException(definition.EndpointSecret is not null ? "A webhook's address is a secret reference (endpoint_secret); a delivery channel resolves it when it sends." : "The connection is not of kind http with an endpoint.", nameof(definition));
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
        var endpoint = definition.Endpoint.EndsWith('/') ? definition.Endpoint : definition.Endpoint + "/";
        var client = new HttpClient(handler) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(definition.Timeouts.CommandS) };
        return new HttpConnectionClient(client, tokens, certificate);
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
