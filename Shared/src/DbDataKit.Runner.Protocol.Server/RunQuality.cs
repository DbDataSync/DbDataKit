using DbDataKit.State;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// The results of the data quality checks one run evaluated (plan 35 I3), recorded in the runner's <c>quality</c> module: the task holds no connection to the runner's state, so it sends each result through the protocol and the runner writes it with
/// <see cref="QualityStore.Record"/> (the result and the check's status and block in one transaction, the store's block rules). Every result is stored under <b>this run's id</b>, whatever the task says, so a result always names the run that made it;
/// what a result may say is checked here (a status, severity and <c>on_error</c> the module knows, bounded text), and anything else is refused with nothing written.
/// </summary>
internal sealed class RunQuality(StateDatabase database, Guid runId)
{
    /// <summary>The longest dataset identity, check id and message a result may carry.</summary>
    public const int MaxIdentity = 2000, MaxCheckId = 200, MaxMessage = 4000, MaxSampleKey = 500;

    private readonly QualityStore _store = new(database);

    public decimal? Baseline(string dataset, string checkId)
    {
        Name(dataset, checkId);
        return _store.Baseline(dataset, checkId);
    }

    public CheckStatusRow? Status(string dataset, string checkId)
    {
        Name(dataset, checkId);
        return _store.Status(dataset, checkId);
    }

    public IReadOnlyList<CheckStatusRow> Blocks() => _store.Blocks();

    /// <summary>Records one result under this run's id and answers the check's status afterwards.</summary>
    public CheckStatusRow Record(CheckResultRecord result)
    {
        Name(result.Dataset, result.CheckId);
        if (result.Severity is not ("warn" or "block")) throw new ArgumentException($"The severity of the check '{result.CheckId}' is '{result.Severity}'; it is warn or block.");
        if (result.OnError is not ("alert" or "block")) throw new ArgumentException($"The on_error of the check '{result.CheckId}' is '{result.OnError}'; it is alert or block.");
        if (result.Message is { Length: > MaxMessage }) throw new ArgumentException($"The message of the check '{result.CheckId}' is longer than {MaxMessage} characters.");
        if ((result.SampleKeys ?? []).Any(k => k is null || k.Length > MaxSampleKey)) throw new ArgumentException($"A sample key of the check '{result.CheckId}' is empty or longer than {MaxSampleKey} characters.");
        return _store.Record(result with { RunId = runId.ToString("D"), SampleKeys = [.. (result.SampleKeys ?? []).Take(QualityStore.MaxSampleKeys)] });
    }

    private static void Name(string dataset, string checkId)
    {
        if (string.IsNullOrWhiteSpace(dataset) || dataset.Length > MaxIdentity) throw new ArgumentException($"A result names its dataset by its lineage identity (1 to {MaxIdentity} characters).");
        if (string.IsNullOrWhiteSpace(checkId) || checkId.Length > MaxCheckId) throw new ArgumentException($"A result names its check by its id (1 to {MaxCheckId} characters).");
    }
}
