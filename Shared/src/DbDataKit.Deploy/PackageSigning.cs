using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataKit.Deploy;

/// <summary>
/// The signature of a Runner package's version, made as it is published (<see cref="RunnerPublishClient.PublishAsync"/>): the Runner is asked whether it trusts the key for packages (<c>GET trust/integration</c>), and the version is
/// signed when it does (<see cref="DefinitionSigning"/>, over the build record too when one is sent), published unsigned when it does not and does not require signatures, and refused before it is sent, naming the key and what an
/// administrator does, when it requires them and does not trust the key. A Runner that does not answer the question gets the version unsigned, and its own rules decide.
/// </summary>
public static class PackageSigning
{
    /// <summary>The definition kind of a Runner package.</summary>
    public const string Kind = "integration";

    public static PackageSigner For(IDefinitionsLink link, ConsoleSigningKey key) => async (package, manifest, files, build, ct) =>
    {
        var trust = await new DefinitionsClient(link, key).TrustAsync(Kind, ct);
        if (trust is not { } known) return null;
        if (known.Trusted) return DefinitionSigning.Sign(key, Kind, package, manifest, files, build);
        if (!known.Required) return null;
        throw new RunnerClientException($"The runner refused the package {package}: it requires signed versions and does not trust the key {key.Fingerprint} for packages. An administrator of the runner trusts it with "
            + $"PUT /api/v1/trust/{Kind}/keys ({{\"public_key\": \"{Url.Encode(key.PublicKey)}\", \"label\": \"...\"}}), or turns require_signatures off.");
    };
}
