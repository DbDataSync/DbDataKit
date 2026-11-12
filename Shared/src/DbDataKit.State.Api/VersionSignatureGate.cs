using DbDataKit.State;

namespace DbDataKit.State.Api;

/// <summary>
/// What an instance checks of a version when it prepares it (plan 30 PB03, decision 5: "every instance verifies at preparation"). The rule, which is the same for every host:
/// <list type="number">
/// <item>a version that <b>carries a signature</b> is verified again against the group's trust store, over the manifest and the files as they are stored now: a key the kind does not trust, or a signature that does not hold (a manifest or a file changed after it was signed), refuses the version. A key revoked since the version was published does not refuse it: a revoked key refuses new versions, and versions it signed stay;</item>
/// <item>a version <b>without a signature</b> is refused only where the host says it needs one (<paramref name="unsignedRefused"/>: the Gateway, where an administrator set the kind to <c>required</c>; the runner, where <c>require_signatures</c> is on). The per-kind routes that predate the shared API (plan 28c E22 retires them) publish unsigned versions, so a kind nobody asked signatures for keeps serving them.</item>
/// </list>
/// A refused version is not served; the one served before keeps serving, and the host reports why.
/// </summary>
public sealed class VersionSignatureGate(Func<string, string, int, IReadOnlyList<ArtifactRef>> artifacts, DefinitionTrustStore trust, Func<string, bool> unsignedRefused)
{
    private readonly TrustStoreVerifier _verifier = new(trust, allowRevoked: true);

    /// <summary>The sentence that refuses <paramref name="version"/>, or null when it may be prepared.</summary>
    public string? Refusal(DefinitionVersion version)
    {
        var label = $"version {version.Version} of the {version.Kind} '{version.Name}'";
        if (string.IsNullOrWhiteSpace(version.Signature))
            return unsignedRefused(version.Kind) ? $"{label} is unsigned and signatures are required for {version.Kind}: publish it signed with a key an administrator trusts, or have an administrator make signatures optional for {version.Kind}" : null;
        var verdict = _verifier.Verify(new VersionSignatureRequest(version.Kind, version.Name, version.Content, artifacts(version.Kind, version.Name, version.Version), version.Signature));
        return verdict.State == SignatureState.Verified ? null : $"{label} is not served here because its signature is refused: {verdict.Message ?? "it does not verify"}";
    }
}
