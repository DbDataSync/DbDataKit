using DbDataKit.Web.Trust;
using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Management;

/// <summary>
/// DbDataSync as a server a console manages (plan 13, U08): its instance id and the clients paired with it in <c>server.yaml</c> in the repository root, the pairing endpoints, and the token check that lets a console's
/// call in as the person it names. A call with a token is the same API call a person's browser would make with a session; the token's roles become the product's two (an administrator where it holds admin, a viewer where it
/// reads), through <c>SessionAuthenticationHandler</c>. Nothing here changes how a person signs in to Sync itself.
/// </summary>
public sealed class SyncManagement(string serverYaml, string? configuredFingerprint, Func<Uri?> address)
{
    public const string FileName = "server.yaml";
    public const string ConfiguredFingerprintKey = "DbDataSync:Management:CertificateFingerprint";

    private static string ProgramVersion { get; } =
        (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SyncManagement).Assembly)?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static string ServerYamlIn(string repoRoot) => Path.Combine(repoRoot, FileName);

    public string InstanceId { get; } = ServerIdentity.InstanceId(serverYaml, "sync", "dbdatasync server");

    public PairingFiles Files { get; } = new(serverYaml);

    public OwnCertificate Certificate { get; } = new(address, configuredFingerprint);

    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ApiOptions>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new SyncManagement(ServerYamlIn(options.RepoRoot), configuration[ConfiguredFingerprintKey], () => FirstHttpsAddress(sp.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>(), options));
        });
        services.AddSingleton(sp =>
        {
            var management = sp.GetRequiredService<SyncManagement>();
            return new ConsoleTokenValidator(management.InstanceId, management.Files.Clients(), new MemoryReplayCache());
        });
    }

    /// <summary>The https address the server listens on, as it was started: the one it was given, or the first it bound.</summary>
    private static Uri? FirstHttpsAddress(Microsoft.AspNetCore.Hosting.Server.IServer server, ApiOptions options)
    {
        var bound = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var text in bound.Append(options.Url))
            if (Uri.TryCreate(text.Replace("://*", "://localhost").Replace("://+", "://localhost").Replace("://[::]", "://localhost").Replace("://0.0.0.0", "://localhost"), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return uri;
        return null;
    }

    /// <summary>
    /// Maps pairing and key rotation. They are outside the product's sign-in on purpose: a console that is not paired yet has no token, and what it sends proves it holds the code. Pairing needs the certificate this server
    /// presents; it learns it by asking itself when it is first needed, and a server behind something else that ends TLS is told it in <see cref="ConfiguredFingerprintKey"/>.
    /// </summary>
    public static void Map(WebApplication app)
    {
        var management = app.Services.GetRequiredService<SyncManagement>();
        var group = app.MapGroup("").AllowAnonymous();
        var clients = management.Files.Clients();
        group.MapPairing(new PairingService(management.InstanceId,
            () => management.Certificate.Fingerprint ?? throw new InvalidOperationException("This server cannot read its own certificate yet. Pairing needs it to listen on https; behind a proxy that ends TLS, set " + ConfiguredFingerprintKey + "."),
            clients, management.Files.Codes()));
        group.AnnounceToConsole("dbdatasync", management.InstanceId, clients, management.Files.Codes());
        // What a console asks first: who this is and which version, the same document the other servers' shared host answers at /version.
        group.MapGet("/version", () => Results.Json(new { product = "dbdatasync", version = ProgramVersion, ready = true }));
        group.MapKeyRotation(new KeyRotationService(management.InstanceId, clients, new MemoryReplayCache()));
    }
}
