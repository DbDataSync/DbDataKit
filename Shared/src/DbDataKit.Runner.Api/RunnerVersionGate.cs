using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataKit.Runner.Api;

/// <summary>
/// What a runner checks of an <c>integration</c> version when it prepares it (plan 30 PB03, <see cref="VersionSignatureGate"/>): a signature that is there is verified against the group's trust store, and an unsigned version is refused
/// when <c>require_signatures</c> is on (the runner's own switch; off by default, because a runner that nobody asked for signatures serves what it always served). A refused version fails its staging on this instance and says why.
/// </summary>
public static class RunnerVersionGate
{
    public static Func<DefinitionVersion, string?> Create(StateDatabase state)
    {
        var publisher = new DefinitionPublisher(state, RunnerPackages.Kinds(), new PayloadStore(state), "runner");
        var settings = new GroupSettings(state);
        var gate = new VersionSignatureGate((kind, name, version) => [.. publisher.Artifacts(kind, name, version).Select(a => new ArtifactRef(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId))], new DefinitionTrustStore(state), _ => settings.Get(RunnerPublishing.RequireSignaturesSetting) == "true");
        return gate.Refusal;
    }
}
