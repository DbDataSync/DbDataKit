using DbDataKit.Dialect;

namespace DbDataKit.Transfer.Conformance;

/// <summary>
/// One strategy scenario with its expected result set. Rows use <see cref="ClrContract"/> values. The harness creates the target and stage
/// tables, seeds them, applies the strategy twice (idempotency) and compares the target with <see cref="Expected"/> as a multiset.
/// </summary>
public sealed record StrategyCase(
    string Name,
    string Strategy,
    TransferSchema Schema,
    IReadOnlyList<object?[]> TargetRows,
    IReadOnlyList<object?[]> StageRows,
    IReadOnlyList<object?[]> Expected)
{
    /// <summary>The target gets a PRIMARY KEY on the key columns (ON CONFLICT and ON DUPLICATE KEY need one).</summary>
    public bool UniqueKeyOnTarget { get; init; }
    /// <summary>Fills in strategy parameters (range bounds, options) on the base request.</summary>
    public Func<ApplyRequest, ApplyRequest>? Configure { get; init; }
    /// <summary>Resolves values from the live target before building (the watermark). Receives the base request, the dialect and a scalar runner.</summary>
    public Func<ApplyRequest, ISqlDialect, Func<SqlStatement, Task<object?>>, Task<ApplyRequest>>? Resolve { get; init; }
    /// <summary>The apply must fail with this exception type and leave the target unchanged (guard breach). Expected is then the seeded target.</summary>
    public Type? ExpectedFailure { get; init; }
    /// <summary>When false the second run is not compared with Expected (non-idempotent strategies only; none ship today).</summary>
    public bool Idempotent { get; init; } = true;
}

public sealed record ConformanceResult(string Case, string Strategy, bool Passed, IReadOnlyList<string> Differences, bool SecondRunIdempotent);

/// <summary>An engine the suite can run on. <see cref="OpenAsync"/> must return a fresh, open connection to an empty database/schema each call.</summary>
public sealed record ConformanceEngine(string Name, ISqlDialect Dialect, EngineVersion? Version, Func<CancellationToken, Task<System.Data.Common.DbConnection>> OpenAsync);
