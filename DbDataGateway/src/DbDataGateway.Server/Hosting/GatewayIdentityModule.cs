using System.Net;
using System.Security.Cryptography;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The <c>identity</c> module of the serving layer (plan 20, section 6; O06): <see cref="IdentityHome"/>, the shared identity home, served by the Gateway on a listener of its own, so people sign in without reaching
/// the private console. It is a web application of its own inside the Gateway's process (its own pipeline, sign-in and fallback policy, none of which touch the Flight or management listeners), over the Gateway's
/// state database, with the Gateway's certificate.
/// <para>
/// The signing keys live in the state, protected with the key in the secret <see cref="SigningKeySecret"/>: made on the first start, and copied to every other instance of a group that shares the state
/// (<c>dbdatagateway secret set dbdata:gateway:identity:signing-keys --stdin</c>). An instance with another key refuses to start, naming the secret.
/// </para>
/// </summary>
public sealed class GatewayIdentityModule(
    IOptionsMonitor<GatewayOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers, GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    /// <summary>The secret that holds the key the signing keys are protected with.</summary>
    public const string SigningKeySecret = "dbdata:gateway:identity:signing-keys";

    /// <summary>The home's own sign-in: its cookie, its configuration section, and what a person is told to run for an invitation.</summary>
    public static readonly AuthProduct Auth = new("DbDataGatewayIdentity", "dbdatagateway identity invite --email <address>");

    private readonly ILogger _log = loggers.CreateLogger("DbDataGateway.Identity");
    private WebApplication? _app;

    /// <summary>The address people reach the home at: the setting, or the first TLS host name (localhost when there is none) on the module's port.</summary>
    public static Uri PublicUrlOf(GatewayOptions options) =>
        !string.IsNullOrWhiteSpace(options.Identity.PublicUrl)
            ? new Uri(options.Identity.PublicUrl.TrimEnd('/') + "/")
            : new UriBuilder(Uri.UriSchemeHttps, options.Tls.Hostnames.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "localhost", options.Identity.Port).Uri;

    /// <summary>Where the first-run invitation is written while the home has nobody: <c>&lt;data directory&gt;/identity</c>.</summary>
    public static string DirectoryOf(GatewayOptions options) => Path.Combine(GatewayHost.ResolveDataDirectory(options), "identity");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var publicUrl = PublicUrlOf(current);
        var protector = await ProtectorAsync(current, cancellationToken);

        // the Flight listener loads the certificate when Kestrel starts, which is after the hosted services: the home may be first
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, GatewayHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        // Its settings are the Gateway's (passed in here), so it reads no settings files of its own and watches none: a file watcher is an inotify instance on Linux, of which a user has 128.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggers));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(current.Identity.Address), current.Identity.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificateSelector = (_, _) => certificate.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
        });
        var directory = DirectoryOf(current);
        Directory.CreateDirectory(directory);
        // the upstreams people may sign in through besides a passkey (O06b)
        var entra = current.Identity.Entra is { Authority: { Length: > 0 } authority } e
            ? new EntraUpstream(authority, e.ClientId!, secrets.ResolveSecret(SecretsRuntime.EntraSecretKey, e.ClientSecret), e.TrustEmail)
            : null;
        var windows = current.Identity.Windows.Enabled ? new WindowsUpstream() : null;
        builder.AddIdentityHome(new IdentityHomeOptions(publicUrl, directory, _ => state, _ => protector, Auth, Entra: entra, Windows: windows, Tokens: new AccessTokenPolicy(current.Identity.AccessTokenMaxDays)));

        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy")).AllowAnonymous();
        app.MapIdentityHome(line => _log.LogWarning("{Problem}", line));
        app.MapFallback(() => Results.Text("The identity home of this DbDataGateway. Its endpoints are under /.well-known/openid-configuration and /identity/.", "text/plain")).AllowAnonymous();
        await app.StartAsync(cancellationToken);
        _app = app;
        _log.LogInformation("Identity home on {Address}:{Port}; its issuer is {Issuer}; sign-in with a passkey{Entra}{Windows}.", current.Identity.Address, current.Identity.Port, publicUrl.GetLeftPart(UriPartial.Authority),
            entra is null ? "" : ", through Entra ID", windows is null ? "" : ", with Windows");
    }

    /// <summary>
    /// The protector from the secret store, the key made there on a first start. A home whose state already holds signing keys made with another key (a second instance given no copy of the group's, or a
    /// different one) is a configuration error that names the secret; the key made for it is removed again.
    /// </summary>
    private async Task<KeySigningKeyProtector> ProtectorAsync(GatewayOptions current, CancellationToken cancellationToken)
    {
        var store = SecretsRuntime.OpenStore(current);
        var name = SecretName.Parse(SigningKeySecret);
        var made = false;
        using var held = await store.TryResolveAsync(SecretRef.Parse("secret:" + SigningKeySecret), cancellationToken);
        string text;
        if (held is null)
        {
            text = KeySigningKeyProtector.NewKey();
            using var value = SecretValue.From(text);
            await store.SetAsync(name, value, cancellationToken);
            made = true;
        }
        else text = held.RevealString();

        KeySigningKeyProtector protector;
        try { protector = KeySigningKeyProtector.FromBase64(text); }
        catch (FormatException ex) { throw new SecretConfigurationException(SigningKeySecret, $"The secret {SigningKeySecret} is not a key: {ex.Message} Remove it to have one made, if this home has no signing key yet."); }
        try
        {
            new SigningKeys(state, protector).Current();
        }
        catch (CryptographicException)
        {
            if (made) await store.DeleteAsync(name, cancellationToken);
            throw new SecretConfigurationException(SigningKeySecret,
                $"The identity home's signing keys in the state database were protected with another key than the one in {SigningKeySecret}. Every instance that shares the state needs the same key: " +
                $"copy it from the instance that made it (dbdatagateway secret export there) into this one's store (dbdatagateway secret set {SigningKeySecret} --stdin).");
        }
        return protector;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>The home's log lines go to the Gateway's log (with its redaction), under their own categories.</summary>
    private sealed class ForwardingLoggerProvider(ILoggerFactory target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => target.CreateLogger(categoryName);
        public void Dispose() { }
    }
}
