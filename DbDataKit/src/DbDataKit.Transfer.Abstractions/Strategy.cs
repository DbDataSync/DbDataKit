using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>A bound parameter. <see cref="Marker"/> is the placeholder text in the statement; values are never part of statement text.</summary>
public sealed record SqlParameter(string Marker, string Name, CanonicalType Type, object? Value);

public enum StatementRole
{
    /// <summary>A write that is part of the apply.</summary>
    Apply,
    /// <summary>Returns one number (rows in scope); used by the delete guard.</summary>
    CountScope,
    /// <summary>Returns one value (for example the watermark); not a write.</summary>
    Scalar,
}

/// <summary>
/// One statement and what the gate needs to check and log it. <c>Payload</c> is whatever the caller's own executor needs beyond the portable parameters (Build keeps its driver parameter types there): the gate never
/// reads it and never logs it. <c>ParametersAreRows</c> says the parameters are the rows of a bulk load, so the log records how many values were sent, never the values.
/// </summary>
public sealed record SqlStatement(string Id, string Text, IReadOnlyList<SqlParameter> Parameters, WriteIntent Intent, StatementRole Role = StatementRole.Apply, object? Payload = null, bool ParametersAreRows = false);

/// <summary>Guard evaluated after the delete statement of a key-reconcile: a breach rolls the transaction back.</summary>
public sealed record DeleteGuard(double MaxRatio = 0.5, bool Disabled = false)
{
    public static readonly DeleteGuard None = new(1.0, true);

    /// <summary>An empty scope always passes (0/0 is not a runaway delete). Same rule as Sync's ratio guard.</summary>
    public bool Allows(long scopeCount, long deleted) =>
        Disabled || scopeCount <= 0 || (double)deleted / scopeCount <= MaxRatio;
}

/// <summary>
/// The ordered statements of one apply. The runner executes them in order and, when <see cref="Transactional"/>, wraps them in one
/// transaction. <see cref="Guard"/> pairs the count statement with the delete statement of a key reconcile.
/// </summary>
public sealed record StatementSet(string Strategy, IReadOnlyList<SqlStatement> Statements, bool Transactional, DeleteGuard? Guard = null, IReadOnlyList<string>? Notes = null);

[Flags]
public enum StrategyCaps
{
    None = 0,
    Transaction = 1,
    KeyColumns = 2,
    UniqueKeyOnTarget = 4,
    RangeColumn = 8,
    WatermarkColumn = 16,
    MergeOrUpsert = 32,
}

public enum MergeStyle { Auto, Merge, OnConflict, OnDuplicateKey }

public sealed record ApplyOptions
{
    /// <summary>full_replace: TRUNCATE instead of DELETE. Not transactional on every engine, so the set is marked non-transactional there.</summary>
    public bool UseTruncate { get; init; }
    public MergeStyle MergeStyle { get; init; } = MergeStyle.Auto;
    public DeleteGuard Guard { get; init; } = new();
}

/// <summary>
/// What to apply. <see cref="Stage"/> already holds the rows (loaded through an <see cref="IStagingProvider"/>) with the columns of <see cref="Schema"/>.
/// Key columns come from <see cref="TransferColumn.IsKey"/>.
/// </summary>
public sealed record ApplyRequest(
    TableName Target,
    TableName Stage,
    TransferSchema Schema,
    EngineVersion? EngineVersion = null,
    ApplyOptions? Options = null,
    string? RangeColumn = null,
    object? RangeStart = null,
    object? RangeEnd = null,
    string? WatermarkColumn = null,
    object? Watermark = null,
    bool WatermarkInclusive = false,
    string? ScopeColumn = null)
{
    public ApplyOptions Opts => Options ?? new ApplyOptions();
}

public interface IApplyStrategy
{
    string Name { get; }
    StrategyCaps Needs { get; }
    /// <summary>Builds the parameterised statements. Throws <see cref="StrategyNotSupportedException"/> or <see cref="ArgumentException"/> (missing keys or columns).</summary>
    StatementSet Build(ApplyRequest request, ISqlDialect dialect);
}
