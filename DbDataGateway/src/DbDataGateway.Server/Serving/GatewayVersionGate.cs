using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// What an instance of the Gateway checks of a published version before it serves it (plan 30 PB03, <see cref="VersionSignatureGate"/>): a signature that is there is verified against the group's trust store, and an unsigned version is
/// refused only for a kind an administrator set to <c>required</c> (<see cref="SignaturePolicy.RequiredByAdministrator"/>): the per-kind routes (plan 28c E22 retires them) publish unsigned versions in groups that never asked for signatures.
/// A refused version is logged like a version in a format this instance does not read, and the one served before keeps serving.
/// </summary>
public static class GatewayVersionGate
{
    public static Func<DefinitionVersion, string?> Create(StateDatabase state)
    {
        var publisher = new DefinitionPublisher(state, ServingKinds.Register(new DefinitionKinds()), new PayloadStore(state), ServingKinds.TargetType);
        var policy = new SignaturePolicy(new GroupSettings(state));
        var gate = new VersionSignatureGate((kind, name, version) => [.. publisher.Artifacts(kind, name, version).Select(a => new ArtifactRef(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))], new DefinitionTrustStore(state), policy.RequiredByAdministrator);
        return gate.Refusal;
    }
}
