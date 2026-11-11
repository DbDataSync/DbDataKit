namespace DbDataKit.Store;

/// <summary>
/// One rule a write broke, in the suite's error shape: which rule (<c>required</c>, <c>type</c>, <c>length</c>, <c>precision</c>, <c>pattern</c>, <c>min</c>, <c>max</c>, <c>allowed</c>, <c>unique</c>, <c>reference</c>,
/// <c>referenced</c>, <c>key</c>, <c>key_immutable</c>, <c>sum_equals</c>, <c>no_cycle</c>), the entity and attribute it is about, the key of the row, and a sentence a person can act on.
/// </summary>
public sealed record RuleViolation(string Rule, string Entity, string? Attribute, IReadOnlyDictionary<string, object?> Key, string Message);

/// <summary>A write was refused, and nothing was written: every rule it broke is in <see cref="Violations"/>.</summary>
public sealed class WriteRefusedException(IReadOnlyList<RuleViolation> violations)
    : Exception(string.Join(" ", violations.Select(v => $"[{v.Rule}] {v.Message}")))
{
    public IReadOnlyList<RuleViolation> Violations { get; } = violations;
}

/// <summary>The row changed since the version the write names; read it again and make the change on that.</summary>
public sealed class VersionConflictException(string entity, IReadOnlyDictionary<string, object?> key, long expected, long current)
    : Exception($"`{entity}` row {Format(key)} is at version {current}, not {expected}: it changed since it was read.")
{
    public long Expected { get; } = expected;
    public long Current { get; } = current;

    internal static string Format(IReadOnlyDictionary<string, object?> key) => "(" + string.Join(", ", key.Select(k => $"{k.Key}={k.Value}")) + ")";
}

/// <summary>There is no such row to change or delete.</summary>
public sealed class RowNotFoundException(string entity, IReadOnlyDictionary<string, object?> key)
    : Exception($"`{entity}` has no row {VersionConflictException.Format(key)}.");

/// <summary>The model cannot be written yet (a rule the write path does not enforce); stated when the writer is made, so nothing is silently unchecked.</summary>
public sealed class StoreNotSupportedException(string message) : Exception(message);
