namespace DbDataKit.State;

/// <summary>
/// Where an evaluation of checks reads its baselines and writes its results (plan 35 I3): the <c>quality</c> module of the state that owns the results. <see cref="QualityStore"/> is the module itself, for a process that holds that
/// state; a Runner task, which holds no connection to the Runner's state, writes through the runner protocol (the kit's <c>EngineQuality</c>), and the Runner records each result with <see cref="QualityStore.Record"/>, so the block rules
/// are the store's whichever way a result comes.
/// </summary>
public interface IQualityResults
{
    /// <summary>The baseline of a <c>row_count_change</c> check (<see cref="QualityStore.Baseline"/>).</summary>
    Task<decimal?> BaselineAsync(string dataset, string checkId, CancellationToken ct = default);

    /// <summary>The check's status, or null when it has none (<see cref="QualityStore.Status"/>).</summary>
    Task<CheckStatusRow?> StatusAsync(string dataset, string checkId, CancellationToken ct = default);

    /// <summary>Stores the result and moves the check's status and block in one transaction; the status afterwards (<see cref="QualityStore.Record"/>).</summary>
    Task<CheckStatusRow> RecordAsync(CheckResultRecord result, CancellationToken ct = default);

    /// <summary>The checks that block now, oldest block first (<see cref="QualityStore.Blocks"/>).</summary>
    Task<IReadOnlyList<CheckStatusRow>> BlocksAsync(CancellationToken ct = default);
}
