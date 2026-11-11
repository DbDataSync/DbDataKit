using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>
/// What <see cref="ArtifactCache.EvictAsync"/> must keep (plan 28c E04): the files of every version a channel serves, staged or can return to, of every package, which includes every active and previous runtime package, because a
/// definition's next run and a rollback of the runtime both need its files. Everything else may go.
/// </summary>
public static class ArtifactKeep
{
    /// <summary>The hashes of the files of every active, previous and staged version of every <c>integration</c> in every channel.</summary>
    public static IReadOnlySet<string> Hashes(DefinitionPublisher publisher)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in publisher.Names(DefinitionVersionSource.Kind))
            foreach (var head in publisher.HeadsOf(DefinitionVersionSource.Kind, name))
                foreach (var version in new[] { head.Active, head.Previous, head.Staged })
                    if (version is { } v)
                        foreach (var artifact in publisher.Artifacts(DefinitionVersionSource.Kind, name, v)) keep.Add(artifact.Sha256.ToLowerInvariant());
        return keep;
    }
}
