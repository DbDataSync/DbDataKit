using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// A staged value whose type no column of the target's kind takes, or that the target would store as a different value. Names the table, the column, the
/// column's declared type and the value's runtime type; never the value.
/// </summary>
public sealed class StagingValueException(string table, string column, string nativeType, Type valueType, string? advice, bool reinterpreted = false)
    : InvalidOperationException(
        $"Staging into {table}: column '{column}' ({nativeType}) cannot take a value of type {valueType.FullName} from the source. " +
        (reinterpreted
            ? "The target engine would store that type as a different value (a number as a date or time, a character as its code, bytes as text), whatever the value. "
            : "The target engine refuses that type for every column of this kind, whatever the value. ") +
        "Change what the mapped source column or its Transform produces to a type the column takes." +
        (advice is null ? "" : " " + advice))
{
    public string Table { get; } = table;
    public string Column { get; } = column;
    public Type ValueType { get; } = valueType;

    /// <summary>True when the engine would have stored the value as something else (A14b), false when it would have refused the type.</summary>
    public bool Reinterpreted { get; } = reinterpreted;
}

/// <summary>
/// The conversion layer (A14a) between the loose values Sync's readers produce and the staging columns: per column and runtime type it decides once
/// whether the value goes on to the writer (<b>pass</b>: every value the engine can take, including the ones its provider converts leniently) or
/// fails here with a message that names the table, the column and the type (<b>refuse</b>: a type the engine refuses for every column of the kind, or,
/// since A14b, one it would store as a different value: see <see cref="StagingValueRules"/>). It converts nothing: see <c>architecture/staging-value-types.md</c> section 3 for why.
/// <para>
/// The cost per cell is one type comparison against the column's last accepted type.
/// </para>
/// </summary>
public sealed class StagingValueGate
{
    private readonly EngineId _engine;
    private readonly string _table;
    private readonly string[] _columns;
    private readonly string[] _nativeTypes;
    private readonly CanonicalType[] _types;
    private readonly string? _advice;
    private readonly Type?[] _passed;

    private StagingValueGate(EngineId engine, string table, string[] columns, string[] nativeTypes, CanonicalType[] types, string? advice)
    {
        _engine = engine; _table = table; _columns = columns; _nativeTypes = nativeTypes; _types = types; _advice = advice;
        _passed = new Type?[columns.Length];
    }

    /// <summary>The number of leading columns of the staging schema the gate looks at (the mapped data columns; the ordering and operation columns are Sync's own).</summary>
    public int Columns => _columns.Length;

    /// <param name="advice">Appended to a refusal; PostgreSQL's names the other staging Kind, which converts on the server.</param>
    public static StagingValueGate For(
        EngineId engine, string table, IReadOnlyList<string> mappedTargetColumns, IReadOnlyDictionary<string, string> nativeTypes, TransferSchema schema, string? advice = null)
    {
        var columns = mappedTargetColumns.ToArray();
        return new StagingValueGate(
            engine, table, columns, [.. columns.Select(c => nativeTypes[c])], [.. columns.Select((_, i) => schema[i].Type)], advice);
    }

    /// <summary>Throws <see cref="StagingValueException"/> when the value's type is refused for this column. <paramref name="value"/> is not null or DBNull.</summary>
    public void Check(int column, object value)
    {
        var type = value.GetType();
        if (ReferenceEquals(_passed[column], type)) return;
        var verdict = StagingValueRules.Verdict(_engine, _types[column], type);
        if (verdict != StagingValueRules.Outcome.Pass)
        {
            var reinterpreted = verdict == StagingValueRules.Outcome.Reinterpreted;
            throw new StagingValueException(_table, _columns[column], _nativeTypes[column], type, reinterpreted ? null : _advice, reinterpreted);
        }
        _passed[column] = type;
    }
}
