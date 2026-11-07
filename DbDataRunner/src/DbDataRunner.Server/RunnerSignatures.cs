using DbDataKit.State;

namespace DbDataRunner.Server;

/// <summary>What a signature check found: <see cref="Unsigned"/> (no signature, or none this build can check), <see cref="Verified"/> or <see cref="Invalid"/> (there is one and it does not hold).</summary>
public enum SignatureState
{
    Unsigned,
    Verified,
    Invalid,
}

/// <summary>The outcome of <see cref="IVersionSignatureVerifier.Verify"/>: the state, who signed it when it verified (the publisher's key id), and a sentence for a refusal.</summary>
public sealed record SignatureVerdict(SignatureState State, string? Signer = null, string? Message = null)
{
    public string Word => State.ToString().ToLowerInvariant();
}

/// <summary>What a signature covers (plan 30 section 6.2): the manifest, which lists the launch and tasks, and every artifact's path and SHA-256, plus the signature text the publisher sent, if any.</summary>
public sealed record VersionSignatureRequest(string Kind, string Name, string Manifest, IReadOnlyList<ArtifactRef> Artifacts, string? Signature);

/// <summary>
/// The seam for plan 30 section 6.2: the publisher signs the version's manifest with its own key (Ed25519, as the update system signs releases) and the runner verifies it against the group's trust store. <b>Not built in this
/// slice</b>: the default, <see cref="NoSignatureVerifier"/>, finds every version <see cref="SignatureState.Unsigned"/>, which the publish records (audit, response); the administrator's <c>require_signatures</c> setting then refuses
/// them. The verifier that checks a signature replaces the default when the trust store exists.
/// </summary>
public interface IVersionSignatureVerifier
{
    SignatureVerdict Verify(VersionSignatureRequest request);
}

/// <summary>The default verifier: nothing is checked, so every version is recorded as unsigned.</summary>
public sealed class NoSignatureVerifier : IVersionSignatureVerifier
{
    public SignatureVerdict Verify(VersionSignatureRequest request) => new(SignatureState.Unsigned);
}
