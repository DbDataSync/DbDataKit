using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// The runner's <c>quality</c> module, bound to the run (plan 35 I3): a task that evaluates data quality checks (a refresh, after its loads) reads each <c>row_count_change</c> baseline and writes each result here, through the protocol, because the
/// task holds no connection to the runner's state. The runner records a result with the store's block rules and under this run's id, so the results the console shows always name the run that made them. Give it to
/// <c>QualityRunService</c> as its <see cref="IQualityResults"/>.
/// </summary>
public sealed class EngineQuality : IQualityResults
{
    private readonly RunContext _context;

    internal EngineQuality(RunContext context) => _context = context;

    public Task<decimal?> BaselineAsync(string dataset, string checkId, CancellationToken ct = default) => _context.Backend.QualityBaselineAsync(dataset, checkId, ct);

    public Task<CheckStatusRow?> StatusAsync(string dataset, string checkId, CancellationToken ct = default) => _context.Backend.QualityStatusAsync(dataset, checkId, ct);

    public Task<CheckStatusRow> RecordAsync(CheckResultRecord result, CancellationToken ct = default) => _context.Backend.QualityRecordAsync(result, ct);

    public Task<IReadOnlyList<CheckStatusRow>> BlocksAsync(CancellationToken ct = default) => _context.Backend.QualityBlocksAsync(ct);
}
