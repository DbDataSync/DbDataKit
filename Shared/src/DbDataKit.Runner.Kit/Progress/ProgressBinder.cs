using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// Fills a job's <see cref="Progress"/> object from the store at start and works out what changed (plan 28b section 5). Each kept value has an <i>entry</i>: the version it was read at (0: not stored yet) and the value
/// it had then. A member is changed when its value now differs from that; the changed entries are written together, with the run's outcome, by <see cref="SaveAsync"/>.
/// </summary>
internal sealed class ProgressBinder
{
    private readonly IRunBackend _backend;
    private readonly ProgressLayout _layout;
    private readonly List<Entry> _entries = [];

    public ProgressBinder(IRunBackend backend, ProgressLayout layout)
    {
        _backend = backend;
        _layout = layout;
    }

    private abstract class Entry(string name, string itemKey, object target)
    {
        public string Name { get; } = name;

        public string ItemKey { get; } = itemKey;

        public object Target { get; } = target;

        public long Version { get; set; }
    }

    private sealed class CheckpointEntry(string name, string itemKey, object target, IReadOnlyList<ProgressMember> members, string? flatPart) : Entry(name, itemKey, target)
    {
        public IReadOnlyList<ProgressMember> Members { get; } = members;

        /// <summary>The one part a flat member is stored as; null for a keyed item, whose members are the parts.</summary>
        public string? FlatPart { get; } = flatPart;

        public string PartOf(ProgressMember member) => FlatPart ?? member.Name;

        public Dictionary<string, ProgressValue?> Snapshot { get; } = [];
    }

    private sealed class StateEntry(string name, string itemKey, object target, ProgressMember member) : Entry(name, itemKey, target)
    {
        public ProgressMember Member { get; } = member;

        public ProgressValue? Snapshot { get; set; }
    }

    /// <summary>Reads the flat members now and attaches the loaders of the keyed ones (a key is read when first used).</summary>
    public async Task BindAsync(Progress progress, CancellationToken ct)
    {
        foreach (var member in _layout.Flat)
        {
            if (member.IsCheckpoint)
            {
                var entry = new CheckpointEntry(member.Name, "", progress, [member], ProgressLayout.ValuePart);
                var stored = await _backend.ReadCheckpointAsync(member.Name, "", ct).ConfigureAwait(false);
                Apply(entry, stored);
                Add(entry);
            }
            else
            {
                var entry = new StateEntry(member.Name, "", progress, member);
                await LoadStateAsync(entry, ct).ConfigureAwait(false);
                Add(entry);
            }
        }

        foreach (var keyed in _layout.Keyed)
        {
            var keyedObject = (IKeyed)keyed.Property.GetValue(progress)!;
            keyedObject.Attach(key => LoadKey(keyed, key, ct));
        }
    }

    private object LoadKey(KeyedMember keyed, string key, CancellationToken ct)
    {
        var item = Activator.CreateInstance(keyed.ItemType)!;
        if (keyed.Checkpoints.Count > 0)
        {
            var entry = new CheckpointEntry(keyed.Name, key, item, keyed.Checkpoints, null);
            Apply(entry, _backend.ReadCheckpointAsync(keyed.Name, key, ct).GetAwaiter().GetResult());
            Add(entry);
        }

        foreach (var state in keyed.States)
        {
            var entry = new StateEntry(keyed.StateName(state), key, item, state);
            LoadStateAsync(entry, ct).GetAwaiter().GetResult();
            Add(entry);
        }

        return item;
    }

    private void Add(Entry entry)
    {
        lock (_entries) _entries.Add(entry);
    }

    private void Apply(CheckpointEntry entry, Checkpoint? stored)
    {
        entry.Version = stored?.Version ?? 0;
        foreach (var member in entry.Members)
        {
            ProgressValue? value = null;
            stored?.Parts.TryGetValue(entry.PartOf(member), out value);
            entry.Snapshot[entry.PartOf(member)] = value;
            if (value is not null) member.Property.SetValue(entry.Target, TypeMap.FromValue(value, member.Property.PropertyType, _backend.GetPayload));
        }
    }

    private async Task LoadStateAsync(StateEntry entry, CancellationToken ct)
    {
        var stored = await _backend.ReadStateAsync(entry.Name, entry.ItemKey, ct).ConfigureAwait(false);
        entry.Version = stored?.Version ?? 0;
        entry.Snapshot = stored?.Value;
        if (stored is { } found) entry.Member.Property.SetValue(entry.Target, TypeMap.FromValue(found.Value, entry.Member.Property.PropertyType, _backend.GetPayload));
    }

    /// <summary>The value a member holds now, or null for none: a null, or the default of a value type that was never stored (so "never set" is not written as zero).</summary>
    private static ProgressValue? Current(ProgressMember member, object target, ProgressValue? snapshot)
    {
        var value = member.Property.GetValue(target);
        if (snapshot is null && TypeMap.IsDefault(value, member.Property.PropertyType)) return null;
        return TypeMap.ToValue(value, member.Property.PropertyType, member.Type);
    }

    private sealed record Pending(CheckpointEntry? Checkpoint, Dictionary<string, ProgressValue?>? Parts, StateEntry? State, ProgressValue? StateValue);

    private List<Pending> Collect()
    {
        var pending = new List<Pending>();
        lock (_entries)
        {
            foreach (var entry in _entries)
            {
                if (entry is CheckpointEntry checkpoint)
                {
                    var current = checkpoint.Members.ToDictionary(checkpoint.PartOf, m => Current(m, checkpoint.Target, checkpoint.Snapshot[checkpoint.PartOf(m)]));
                    if (current.Any(c => !TypeMap.Same(c.Value, checkpoint.Snapshot[c.Key])) && current.Any(c => c.Value is not null))
                        pending.Add(new Pending(checkpoint, current, null, null));
                }
                else if (entry is StateEntry state)
                {
                    var current = Current(state.Member, state.Target, state.Snapshot);
                    if (current is not null && !TypeMap.Same(current, state.Snapshot)) pending.Add(new Pending(null, null, state, current));
                }
            }
        }

        return pending;
    }

    /// <summary>Whether any member changed since it was read or last saved.</summary>
    public bool HasChanges() => Collect().Count > 0;

    /// <summary>
    /// Writes what changed, in one transaction, and with it the run's outcome when <paramref name="end"/> is given (a run that does not end well passes none and does not call this). False when a version moved:
    /// nothing was written.
    /// </summary>
    public async Task<bool> SaveAsync(TaskEnd? end, CancellationToken ct)
    {
        var pending = Collect();
        if (pending.Count == 0)
        {
            if (end is not null) await _backend.FinishAsync(end, ct).ConfigureAwait(false);
            return true;
        }

        var checkpoints = pending.Where(p => p.Checkpoint is not null)
            .Select(p => new CheckpointWrite(p.Checkpoint!.Name, p.Checkpoint.ItemKey, p.Parts!.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value!), p.Checkpoint.Version)).ToList();
        var states = pending.Where(p => p.State is not null).Select(p => new StateWrite(p.State!.Name, p.State.ItemKey, p.StateValue!, p.State.Version)).ToList();
        if (!await _backend.CommitProgressAsync(checkpoints, states, end, ct).ConfigureAwait(false)) return false;

        foreach (var p in pending)
        {
            if (p.Checkpoint is { } checkpoint)
            {
                checkpoint.Version++;
                foreach (var (name, value) in p.Parts!) checkpoint.Snapshot[name] = value;
            }
            else if (p.State is { } state)
            {
                state.Version++;
                state.Snapshot = p.StateValue;
            }
        }

        return true;
    }
}
