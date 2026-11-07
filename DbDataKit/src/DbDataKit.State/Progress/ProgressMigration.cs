using System.Globalization;

namespace DbDataKit.State;

/// <summary>One part of a declared checkpoint.</summary>
/// <param name="Default">What a new key gets (and an existing one, when the part is new): the value as text in the part's type. Null: no default.</param>
/// <param name="RenamedFrom">The part's name in the previous version, when the declaration says it was renamed.</param>
/// <param name="Format">How text is read when the part's type changed from text to a date or a time (<c>yyyy-MM-dd</c>).</param>
/// <param name="Reset">True: in this version the next run starts the part from its default (a full reload).</param>
public sealed record DeclaredPart(ProgressType Type, string? Default = null, string? RenamedFrom = null, string? Format = null, bool Reset = false);

/// <summary>A checkpoint a task declares in its package manifest.</summary>
/// <param name="Per">The parts of the key it is kept per (<c>[customer]</c>, <c>[customer, region]</c>); empty: one checkpoint.</param>
/// <param name="KeyFill">The value an existing key gets for a key part the declaration adds (<c>all-regions</c>); null: the checkpoint is reset instead.</param>
public sealed record DeclaredCheckpoint(string Name, IReadOnlyDictionary<string, DeclaredPart> Parts, IReadOnlyList<string>? Per = null, string? RenamedFrom = null, bool Reset = false, string? KeyFill = null);

/// <summary>A state value a task declares.</summary>
public sealed record DeclaredState(string Name, ProgressType Type, IReadOnlyList<string>? Per = null, string? Default = null, string? RenamedFrom = null, string? Format = null, bool Reset = false, string? KeyFill = null);

/// <summary>The progress a version of a task declares: its checkpoints and state values, typed. The version of the progress is this declaration; the author numbers nothing.</summary>
public sealed record ProgressDeclaration(IReadOnlyList<DeclaredCheckpoint> Checkpoints, IReadOnlyList<DeclaredState> States)
{
    public static ProgressDeclaration Empty { get; } = new([], []);
}

public enum MigrationKind
{
    AddValue,
    DropValue,
    RenameValue,
    ConvertValue,
    ResetValue,
    KeysAdded,
    KeysRemoved,
    RemoveWhole,
}

/// <summary>One change the planner works out. <see cref="LosesProgress"/> says whether it throws away what a task had got to.</summary>
public sealed record MigrationStep(MigrationKind Kind, string Target, string Detail, bool LosesProgress);

/// <summary>What changing from one declaration to another does, worked out before anything is touched.</summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationStep> Steps)
{
    /// <summary>What the change loses: listed when the version is staged, and activation asks an administrator to confirm it.</summary>
    public IReadOnlyList<MigrationStep> Losses => [.. Steps.Where(s => s.LosesProgress)];

    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>A change the standard rules cannot make, naming the value (so the staging fails, not a run).</summary>
public sealed class ProgressMigrationException(string message) : InvalidOperationException(message);

/// <summary>
/// Plans and applies the change of a task's progress between two versions of its declaration (plan 29 section 4.3), the way Build plans a table change from the declared shape against the live one.
/// Standard changes need no code: a value added gets its default, one removed is dropped (its history kept), one renamed (<c>renamed_from</c>) is moved, a type widened (int to decimal, date to time) is
/// converted, text becomes a date or a time through a declared format, a key part added is filled by a declared rule, and a value can be reset on purpose. A change that cannot be made is refused naming the
/// value. A change that loses progress is listed, and applying it takes a confirmation.
/// </summary>
public static class ProgressMigration
{
    public static MigrationPlan Plan(ProgressDeclaration old, ProgressDeclaration @new)
    {
        var steps = new List<MigrationStep>();
        var seenNew = new HashSet<string>(StringComparer.Ordinal);

        foreach (var next in @new.Checkpoints)
        {
            var before = FindCheckpoint(old, next);
            if (!seenNew.Add("c:" + next.Name)) throw new ProgressMigrationException($"The declaration names the checkpoint '{next.Name}' twice.");
            if (before is null)
            {
                steps.Add(new MigrationStep(MigrationKind.AddValue, next.Name, "a new checkpoint; it starts empty", false));
                continue;
            }

            if (before.Name != next.Name) steps.Add(new MigrationStep(MigrationKind.RenameValue, next.Name, $"renamed from '{before.Name}'", false));
            KeyChanges(steps, next.Name, before.Per, next.Per, next.KeyFill);
            if (next.Reset) steps.Add(new MigrationStep(MigrationKind.ResetValue, next.Name, "reset on purpose: the next run starts from the default", true));

            foreach (var (part, declared) in next.Parts)
            {
                var from = before.Parts.TryGetValue(declared.RenamedFrom ?? part, out var found) ? found : null;
                var target = $"{next.Name}.{part}";
                if (from is null) { steps.Add(new MigrationStep(MigrationKind.AddValue, target, declared.Default is null ? "new, with no default: starts empty" : $"new; existing keys get the default '{declared.Default}'", false)); continue; }
                if (declared.RenamedFrom is not null) steps.Add(new MigrationStep(MigrationKind.RenameValue, target, $"renamed from '{declared.RenamedFrom}'", false));
                if (declared.Reset) steps.Add(new MigrationStep(MigrationKind.ResetValue, target, "reset on purpose: the next run starts from the default", true));
                else if (from.Type != declared.Type) steps.Add(Convert(target, from.Type, declared.Type, declared.Format));
            }

            foreach (var (part, _) in before.Parts.Where(p => !next.Parts.Any(n => (n.Value.RenamedFrom ?? n.Key) == p.Key)))
                steps.Add(new MigrationStep(MigrationKind.DropValue, $"{next.Name}.{part}", "removed; its history is kept for a rollback", true));
        }

        foreach (var gone in old.Checkpoints.Where(o => !@new.Checkpoints.Any(n => (n.RenamedFrom ?? n.Name) == o.Name)))
            steps.Add(new MigrationStep(MigrationKind.RemoveWhole, gone.Name, "the checkpoint is no longer declared; its values are removed and its history kept", true));

        foreach (var next in @new.States)
        {
            if (!seenNew.Add("s:" + next.Name)) throw new ProgressMigrationException($"The declaration names the state value '{next.Name}' twice.");
            var before = old.States.FirstOrDefault(o => o.Name == (next.RenamedFrom ?? next.Name));
            if (before is null) { steps.Add(new MigrationStep(MigrationKind.AddValue, next.Name, next.Default is null ? "a new state value; it starts empty" : $"new; existing keys get the default '{next.Default}'", false)); continue; }
            if (before.Name != next.Name) steps.Add(new MigrationStep(MigrationKind.RenameValue, next.Name, $"renamed from '{before.Name}'", false));
            KeyChanges(steps, next.Name, before.Per, next.Per, next.KeyFill);
            if (next.Reset) steps.Add(new MigrationStep(MigrationKind.ResetValue, next.Name, "reset on purpose: the next run starts from the default", true));
            else if (before.Type != next.Type) steps.Add(Convert(next.Name, before.Type, next.Type, next.Format));
        }

        foreach (var gone in old.States.Where(o => !@new.States.Any(n => (n.RenamedFrom ?? n.Name) == o.Name)))
            steps.Add(new MigrationStep(MigrationKind.RemoveWhole, gone.Name, "the state value is no longer declared; it is removed", true));

        return new MigrationPlan(steps);
    }

    private static DeclaredCheckpoint? FindCheckpoint(ProgressDeclaration old, DeclaredCheckpoint next) => old.Checkpoints.FirstOrDefault(o => o.Name == (next.RenamedFrom ?? next.Name));

    private static void KeyChanges(List<MigrationStep> steps, string name, IReadOnlyList<string>? before, IReadOnlyList<string>? after, string? fill)
    {
        before ??= [];
        after ??= [];
        if (before.SequenceEqual(after)) return;
        if (after.Count > before.Count && before.SequenceEqual(after.Take(before.Count)))
            steps.Add(new MigrationStep(MigrationKind.KeysAdded, name, fill is null ? $"the key gains [{string.Join(", ", after.Skip(before.Count))}] with no fill rule: existing keys are reset" : $"the key gains [{string.Join(", ", after.Skip(before.Count))}]; existing keys are filled with '{fill}'", fill is null));
        else
            steps.Add(new MigrationStep(MigrationKind.KeysRemoved, name, $"the key changes from [{string.Join(", ", before)}] to [{string.Join(", ", after)}]: existing keys cannot be mapped and are reset", true));
    }

    private static MigrationStep Convert(string target, ProgressType from, ProgressType to, string? format)
    {
        if (Converter(from, to, format) is null)
            throw new ProgressMigrationException($"'{target}' changes from {from} to {to}, which the standard rules cannot convert" + (from == ProgressType.Text && to is ProgressType.Date or ProgressType.Time ? $" without a format: declare format: \"yyyy-MM-dd\" (or the shape the text has)." : ". Declare reset: true to start it again from its default, or supply a migration function."));
        return new MigrationStep(MigrationKind.ConvertValue, target, $"{from} becomes {to}" + (format is null ? "" : $" through '{format}'"), false);
    }

    /// <summary>How a value of one type becomes another, or null when the standard rules have none.</summary>
    internal static Func<ProgressValue, ProgressValue>? Converter(ProgressType from, ProgressType to, string? format) => (from, to) switch
    {
        _ when from == to => v => v,
        (ProgressType.Int, ProgressType.Decimal) => v => ProgressValue.OfDecimal(v.AsInt),
        (ProgressType.Date, ProgressType.Time) => v => ProgressValue.OfTime(new DateTimeOffset(v.AsDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
        (_, ProgressType.Text) => v => ProgressValue.OfText(v.Canonical()),
        (ProgressType.Text, ProgressType.Int) => v => ProgressValue.OfInt(long.Parse(v.AsText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
        (ProgressType.Text, ProgressType.Decimal) => v => ProgressValue.OfDecimal(decimal.Parse(v.AsText, NumberStyles.Float, CultureInfo.InvariantCulture)),
        (ProgressType.Text, ProgressType.Bool) => v => ProgressValue.OfBool(bool.Parse(v.AsText)),
        (ProgressType.Text, ProgressType.Json) => v => ProgressValue.OfJson(v.AsText),
        (ProgressType.Text, ProgressType.Date) when format is not null => v => ProgressValue.OfDate(DateOnly.ParseExact(v.AsText, format, CultureInfo.InvariantCulture)),
        (ProgressType.Text, ProgressType.Time) when format is not null => v => ProgressValue.OfTime(DateTimeOffset.ParseExact(v.AsText, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)),
        _ => null,
    };

    /// <summary>
    /// Applies the change to one task's progress, all or nothing as far as validation goes: first every stored value is converted in memory (the dry run the staging does), and if one cannot be, nothing is
    /// written. A plan that loses progress is applied only with <paramref name="confirmLosses"/>. Each rewrite is a version of its checkpoint, with <paramref name="actor"/> in the history.
    /// </summary>
    public static void Apply(ProgressStore store, ProgressDeclaration old, ProgressDeclaration @new, string actor, bool confirmLosses = false)
    {
        var plan = Plan(old, @new);
        if (plan.Losses.Count > 0 && !confirmLosses)
            throw new ProgressMigrationException("This change loses progress: " + string.Join("; ", plan.Losses.Select(l => $"{l.Target}: {l.Detail}")) + ". Confirm it to go on.");

        foreach (var step in BuildWork(store, old, @new, actor, plan)) step();
    }

    /// <summary>
    /// The dry run of staging (plan 29 section 4.3 step 6): plans the change and converts every stored value in memory, writing nothing. Returns the plan (its losses are what an administrator has to confirm); throws
    /// <see cref="ProgressMigrationException"/> naming the value for a change the standard rules cannot make, or a stored value that does not read as its new type.
    /// </summary>
    public static MigrationPlan DryRun(ProgressStore store, ProgressDeclaration old, ProgressDeclaration @new)
    {
        var plan = Plan(old, @new);
        BuildWork(store, old, @new, "dry-run", plan);
        return plan;
    }

    /// <summary>Whether a migration whose actor starts with <paramref name="actorPrefix"/> left changes for this task that <see cref="Restore"/> can put back.</summary>
    public static bool CanRestore(ProgressStore store, string actorPrefix) => store.LatestMigrationActor(actorPrefix) is not null;

    /// <summary>
    /// Puts back what the migration tagged <paramref name="actorPrefix"/> (the newest one with that prefix) changed in this task's progress: checkpoint parts and state values return to what they were before it, from
    /// <c>checkpoint_history</c> and <c>state_history</c>. A part the migration did not touch keeps its value. Returns how many checkpoints and state values were put back (0: no migration of that name left a trace, nothing was done).
    /// </summary>
    public static int Restore(ProgressStore store, string actorPrefix, string actor)
    {
        if (store.LatestMigrationActor(actorPrefix) is not { } migration) return 0;
        var restored = 0;

        // checkpoints: for every part the migration touched, the value before its first change of the part (null: the part did not exist)
        var changes = store.MigrationChanges(migration);
        foreach (var checkpoint in changes.GroupBy(c => (c.Name, c.ItemKey)))
        {
            var current = store.GetCheckpoint(checkpoint.Key.Name, checkpoint.Key.ItemKey);
            var parts = current is null ? new Dictionary<string, ProgressValue>(StringComparer.Ordinal) : new Dictionary<string, ProgressValue>(current.Parts, StringComparer.Ordinal);
            foreach (var part in checkpoint.GroupBy(c => c.Part))
            {
                var first = part.First();
                if (first.Old is null) parts.Remove(part.Key);
                else parts[part.Key] = ProgressValue.Parse(first.Type, first.Old);
            }

            if (parts.Count == 0)
            {
                if (current is not null) store.DeleteCheckpoint(checkpoint.Key.Name, actor, checkpoint.Key.ItemKey);
            }
            else if (!store.Advance(checkpoint.Key.Name, parts, current?.Version ?? 0, itemKey: checkpoint.Key.ItemKey, actor: actor))
                throw new ProgressMigrationException($"The checkpoint '{checkpoint.Key.Name}' moved while its earlier values were being put back; nothing was changed.");
            restored++;
        }

        // state values have no versions of their own to put back: the migration's changes are undone newest first
        foreach (var (name, itemKey, old, _) in store.MigrationStateChanges(migration).Reverse())
        {
            if (old is null) store.DeleteState(name, itemKey);
            else store.SetState(name, old, itemKey);
            restored++;
        }

        return restored;
    }

    private static List<Action> BuildWork(ProgressStore store, ProgressDeclaration old, ProgressDeclaration @new, string actor, MigrationPlan plan)
    {
        var work = new List<Action>();
        foreach (var before in old.Checkpoints)
        {
            var next = @new.Checkpoints.FirstOrDefault(n => (n.RenamedFrom ?? n.Name) == before.Name);
            foreach (var itemKey in store.ItemKeys(before.Name))
            {
                var current = store.GetCheckpoint(before.Name, itemKey)!;
                if (next is null) { work.Add(() => store.DeleteCheckpoint(before.Name, actor, itemKey)); continue; }
                var newKey = MapKey(itemKey, before.Per, next.Per, next.KeyFill);
                var parts = next.Reset || newKey is null ? [] : Convert(next.Parts, before, current.Parts, plan);
                var target = next.Name;
                work.Add(() =>
                {
                    if (newKey != itemKey || target != before.Name) store.DeleteCheckpoint(before.Name, actor, itemKey);
                    else if (parts.Count == 0) { store.DeleteCheckpoint(before.Name, actor, itemKey); return; }
                    if (newKey is null || parts.Count == 0) return;
                    var existing = store.GetCheckpoint(target, newKey);
                    store.Advance(target, parts, existing?.Version ?? 0, itemKey: newKey, actor: actor);
                });
            }
        }

        foreach (var before in old.States)
        {
            var next = @new.States.FirstOrDefault(n => (n.RenamedFrom ?? n.Name) == before.Name);
            foreach (var itemKey in store.StateKeys(before.Name))
            {
                var current = store.GetState(before.Name, itemKey)!;
                if (next is null) { work.Add(() => { store.RecordStateChange(before.Name, itemKey, current, null, actor); store.DeleteState(before.Name, itemKey); }); continue; }
                var newKey = MapKey(itemKey, before.Per, next.Per, next.KeyFill);
                ProgressValue? value = next.Reset || newKey is null ? null : ConvertOne($"{next.Name}", current, next.Type, next.Format);
                work.Add(() =>
                {
                    store.RecordStateChange(before.Name, itemKey, current, null, actor);
                    store.DeleteState(before.Name, itemKey);
                    if (newKey is not null && value is not null)
                    {
                        store.RecordStateChange(next.Name, newKey, store.GetState(next.Name, newKey), value, actor);
                        store.SetState(next.Name, value, newKey);
                    }
                });
            }
        }

        // the dry run is the construction of the work above: every conversion has happened in memory, and a value that does not convert has already thrown
        return work;
    }

    private static string? MapKey(string itemKey, IReadOnlyList<string>? before, IReadOnlyList<string>? after, string? fill)
    {
        before ??= [];
        after ??= [];
        if (before.SequenceEqual(after)) return itemKey;
        if (after.Count > before.Count && before.SequenceEqual(after.Take(before.Count)) && fill is not null)
            return string.Concat(Enumerable.Repeat('|' + fill, after.Count - before.Count)).Insert(0, itemKey).TrimStart('|');
        return null;   // reset
    }

    private static Dictionary<string, ProgressValue> Convert(IReadOnlyDictionary<string, DeclaredPart> declared, DeclaredCheckpoint before, IReadOnlyDictionary<string, ProgressValue> stored, MigrationPlan _)
    {
        var parts = new Dictionary<string, ProgressValue>(StringComparer.Ordinal);
        foreach (var (part, spec) in declared)
        {
            if (!spec.Reset && stored.TryGetValue(spec.RenamedFrom ?? part, out var value))
                parts[part] = ConvertOne($"{before.Name}.{part}", value, spec.Type, spec.Format);
            else if (spec.Default is not null)
                parts[part] = ProgressValue.Parse(spec.Type, spec.Default);
        }

        return parts;
    }

    private static ProgressValue ConvertOne(string target, ProgressValue value, ProgressType to, string? format)
    {
        if (value.Type == to) return value;
        var convert = Converter(value.Type, to, format) ?? throw new ProgressMigrationException($"'{target}' cannot become {to}.");
        try { return convert(value); }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or System.Text.Json.JsonException)
        {
            throw new ProgressMigrationException($"The stored value of '{target}' ('{value.Canonical()}') does not read as {to}{(format is null ? "" : $" in the format '{format}'")}: nothing was changed.");
        }
    }
}
