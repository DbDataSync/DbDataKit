using DbDataKit.Runner.Execution;
using DbDataKit.Secrets;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.Logging;

namespace DbDataRunner.Server;

/// <summary>
/// The Runner's producer of <c>data.changed/1</c> (plan 24a UC13): when a task's run ends, the datasets it reported writing with the Kit's <c>run.Lineage(reads, writes)</c> are told to the serving groups this server is paired with
/// (<c>gateways.json</c> in the server's directory; <c>gateway add</c>, the console's Link or <c>POST /api/v1/gateways</c>). A write line that is a dataset identity (<c>engine://server/database.schema.table</c>, what
/// <c>LineageIdentity.Dataset</c> gives, the name a Gateway's graph holds) is sent; a free-text name a task chose for a table, and an outside system (<c>external://</c>), are not: no serving group serves them. The server signs as itself (a run has no
/// person), delivery never changes the run, and a runner with no pairing sends nothing.
/// </summary>
public sealed class RunnerDataChanges(IDataChangeNotifier notifier, GatewayPairingsFile pairings)
{
    public const string Product = "runner";
    public const string FileName = "gateways.json";

    public IDataChangeNotifier Notifier => notifier;

    public GatewayPairingsFile Pairings => pairings;

    public static GatewayPairingsFile PairingsIn(string directory) => new(Path.Combine(directory, FileName));

    /// <summary>
    /// The notifier of a runner whose directory is <paramref name="directory"/>: its own key is opened from the runner's secret store only when there is a pairing to send to.
    /// </summary>
    public static RunnerDataChanges Create(string directory, RunnerSecretStore secrets, ILogger? log = null, DataChangeNotifierOptions? options = null)
    {
        var pairings = PairingsIn(directory);
        return new RunnerDataChanges(new DataChangeNotifier(Product, pairings, ct => Identity(secrets).GetOrCreateAsync(ct), log, options), pairings);
    }

    /// <summary>The runner's own identity in its secret store, the key its pairings were made with.</summary>
    public static ConsoleKeyStore Identity(RunnerSecretStore secrets)
    {
        var store = secrets.Open();
        return new ConsoleKeyStore(store, store);
    }

    /// <summary>Whether a line a task reported is a dataset identity (<c>namespace/name</c> with a scheme) rather than a free-text name or an outside system.</summary>
    public static bool IsDataset(string line) =>
        line.Length is > 0 and <= DbDataKit.Serving.DataChangedMessage.MaxLength && line.Contains("://", StringComparison.Ordinal) && !line.StartsWith("external://", StringComparison.Ordinal)
        && !line.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) && line.IndexOf('/', line.IndexOf("://", StringComparison.Ordinal) + 3) > 0;

    /// <summary>A run ended: tells the serving groups the datasets it reported writing. Never throws and never waits for the network.</summary>
    public void Completed(TaskRunResult result)
    {
        try
        {
            if (result.Lineage is not { Count: > 0 } lines) return;
            var datasets = lines.SelectMany(l => l.Writes).Select(w => w.Trim()).Where(IsDataset).Distinct(StringComparer.Ordinal).ToList();
            if (datasets.Count == 0) return;
            notifier.Publish(datasets.Select(d => new DataChange(d, result.RunId.ToString())));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the run is over: not telling the groups is a counted failure of the notifier, never an error of the run
        }
    }
}

/// <summary>An <see cref="ITaskRunner"/> that tells the serving groups what the runs of its inner runner wrote, after each one ends (<see cref="RunnerDataChanges"/>).</summary>
internal sealed class DataChangeRunner(ITaskRunner inner, RunnerDataChanges changes) : ITaskRunner
{
    public async Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
    {
        var result = await inner.RunAsync(request, ct).ConfigureAwait(false);
        changes.Completed(result);
        return result;
    }
}
