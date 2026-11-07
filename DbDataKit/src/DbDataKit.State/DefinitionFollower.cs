namespace DbDataKit.State;

/// <summary>
/// What serves one kind of published definition on an instance (plan 20 §10.2, O03): told the version a channel now serves under a name, or null when it serves none any more. It builds what it serves from the version
/// before it replaces anything, so a version it cannot use leaves the one it was serving; it says so by throwing, and the follower reports it.
/// </summary>
public interface IDefinitionSubscriber
{
    /// <summary>The kind of definition it serves (<c>semantic-model</c>, <c>app</c>).</summary>
    string Kind { get; }

    void Apply(string name, DefinitionVersion? current);
}

/// <summary>What a <see cref="DefinitionFollower"/> reads: the definitions a channel serves and the log of what moved. Both <see cref="DefinitionStore"/> and the module's <see cref="DefinitionStore"/> are one.</summary>
public interface IDefinitionSource
{
    long LatestEventId();

    IReadOnlyList<DefinitionVersion> CurrentOfKind(string kind, string channel = DefinitionStore.Production);

    DefinitionVersion? Current(string kind, string name, string channel = DefinitionStore.Production);

    IReadOnlyList<DefinitionEvent> Changes(long sinceId = 0, int limit = 500);
}

/// <summary>
/// Keeps an instance serving what its channel serves (plan 20 O03): at start it hands each subscriber every definition of its kind, then <see cref="Poll"/> reads the store's change log and hands over each definition
/// that moved. The log is in the state every instance of a group shares, so a publish through any instance, or by a publisher that has since stopped, reaches all of them; nothing here calls the publisher.
/// </summary>
/// <param name="store">The group's definitions.</param>
/// <param name="subscribers">What serves each kind.</param>
/// <param name="problem">Told when a subscriber could not use a version (the one it served before keeps serving): the kind, the name, the version and why.</param>
/// <param name="channel">The channel this instance serves.</param>
public sealed class DefinitionFollower(IDefinitionSource store, IEnumerable<IDefinitionSubscriber> subscribers, Action<string, string, int?, Exception> problem, string channel = DefinitionStore.Production)
{
    private readonly Dictionary<string, IDefinitionSubscriber> _byKind = subscribers.ToDictionary(s => s.Kind, StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _since = -1;

    /// <summary>The last change this instance has applied.</summary>
    public long Since { get { lock (_gate) return _since; } }

    /// <summary>Hands every subscriber what its kind serves now. The change log is read from where it stood before, so nothing published meanwhile is missed (it is applied again by the next poll, which is harmless).</summary>
    public void Start()
    {
        lock (_gate)
        {
            var since = store.LatestEventId();
            foreach (var subscriber in _byKind.Values)
                foreach (var current in store.CurrentOfKind(subscriber.Kind, channel))
                    Apply(subscriber, current.Name, current);
            _since = since;
        }
    }

    /// <summary>Applies what moved since the last poll; the number of definitions handed over.</summary>
    public int Poll()
    {
        lock (_gate)
        {
            if (_since < 0) throw new InvalidOperationException("The follower has not started.");
            var applied = 0;
            while (true)
            {
                var events = store.Changes(_since);
                if (events.Count == 0) return applied;
                // a stage moves nothing this instance serves; the latest word per definition is all that matters: each one moved is read again as it stands now
                foreach (var moved in events.Where(e => e.Channel == channel && e.Action != "stage" && _byKind.ContainsKey(e.Kind)).Select(e => (e.Kind, e.Name)).Distinct())
                {
                    Apply(_byKind[moved.Kind], moved.Name, store.Current(moved.Kind, moved.Name, channel));
                    applied++;
                }
                _since = events[^1].Id;
            }
        }
    }

    private void Apply(IDefinitionSubscriber subscriber, string name, DefinitionVersion? current)
    {
        try { subscriber.Apply(name, current); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { problem(subscriber.Kind, name, current?.Version, ex); }
    }
}
