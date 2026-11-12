using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Serving;

/// <summary>
/// A data change a back-end reports to the serving group (plan 24a UC13, QU8): the dataset that changed, by the lineage identity the suite uses everywhere (<c>namespace/name</c>, for example <c>sqlserver://sql1/sales.dbo.orders</c>),
/// the product that says so and when. It carries no row data and no query text.
/// </summary>
/// <param name="Dataset">The lineage identity of the dataset (<c>DatasetIdentity.ToString()</c>), or a suite object name; never a row.</param>
/// <param name="Product">The product that made the change: <c>sync</c>, <c>build</c>, <c>store</c>, <c>runner</c> or what a site's own producer calls itself.</param>
/// <param name="At">When the change was finished, as the producer says.</param>
/// <param name="Run">The run, load or write that made it, for the audit (optional).</param>
public sealed record DataChangedNotice(string Dataset, string Product, DateTimeOffset At, string? Run = null);

/// <summary>
/// The <c>data.changed/1</c> message (24a UC13): a JSON object with <c>message</c>, <c>dataset</c>, <c>product</c> and optionally <c>run</c> and <c>at</c>, sent over the management channel a producer already has to the serving group (a signed
/// management call, so a forged or replayed one is refused by the channel before it gets here). Any other key is refused by name: the message names a dataset and nothing else, so no row data and no statement text can travel in it.
/// </summary>
public static class DataChangedMessage
{
    public const string Format = "data.changed/1";

    /// <summary>The longest dataset identity or product name accepted.</summary>
    public const int MaxLength = 512;

    private static readonly string[] Keys = ["message", "dataset", "product", "run", "at"];

    public static string Write(DataChangedNotice notice)
    {
        var json = new JsonObject
        {
            ["message"] = Format,
            ["dataset"] = notice.Dataset,
            ["product"] = notice.Product,
            ["at"] = notice.At.ToString("O", CultureInfo.InvariantCulture),
        };
        if (notice.Run is not null) json["run"] = notice.Run;
        return json.ToJsonString();
    }

    /// <summary>Reads a message; false with the problem in words (naming the key) when it is not a valid <c>data.changed/1</c>. A missing <c>at</c> is <paramref name="now"/>.</summary>
    public static bool TryRead(string? text, DateTimeOffset now, out DataChangedNotice notice, out string problem)
    {
        notice = null!;
        problem = "";
        JsonObject? json;
        try { json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { json = null; }
        if (json is null)
        {
            problem = $"A {Format} message is a JSON object with `message`, `dataset` and `product`.";
            return false;
        }

        foreach (var (key, _) in json)
            if (!Keys.Contains(key, StringComparer.Ordinal))
            {
                problem = $"`{key}` is not a key of {Format}; the message names a dataset and carries no row data (keys: {string.Join(", ", Keys.Select(k => $"`{k}`"))}).";
                return false;
            }

        string? Text(string key)
        {
            if (json[key] is not JsonValue v) return null;
            return v.TryGetValue<string>(out var s) ? s.Trim() : null;
        }

        if (Text("message") != Format)
        {
            problem = $"`message` must be `{Format}`.";
            return false;
        }

        var dataset = Text("dataset");
        var product = Text("product");
        if (string.IsNullOrEmpty(dataset) || dataset.Length > MaxLength || dataset.Any(char.IsControl))
        {
            problem = $"`dataset` is the lineage identity of the dataset that changed (at most {MaxLength} characters, one line).";
            return false;
        }

        if (string.IsNullOrEmpty(product) || product.Length > 64 || product.Any(char.IsControl))
        {
            problem = "`product` names the product that made the change (`sync`, `build`, `store`, `runner`).";
            return false;
        }

        var run = Text("run");
        if (run is { Length: > MaxLength } || run?.Any(char.IsControl) == true)
        {
            problem = $"`run` is at most {MaxLength} characters, one line.";
            return false;
        }

        var at = now;
        if (Text("at") is { } atText && !DateTimeOffset.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at))
        {
            problem = "`at` is a time as ISO 8601 text.";
            return false;
        }

        notice = new DataChangedNotice(dataset, product, at, run);
        return true;
    }
}

/// <summary>What the cache learned of the data changes (24a UC13 and the risk note of the plan: "events not arriving" must be visible, so a long time-to-live is not a silent staleness).</summary>
public sealed record ServingCacheActivity(long Received, long Cleared, DateTimeOffset? LastAt, string? LastProduct, string? LastDataset, long Manual, DateTimeOffset? LastManualAt);

/// <summary>What one data change did: the cache datasets it reached and how many entries went. <see cref="Datasets"/> is null when the notice named something the host cannot map, so the whole cache was cleared.</summary>
public sealed record DataChangedOutcome(IReadOnlyList<string>? Datasets, int Removed, bool Everything);

/// <summary>
/// The group's shared record of the epochs of the datasets (24a UC14, owner default: a state module that holds only a per-dataset epoch, never a row of data). Every instance bumps it when it clears a dataset and reads it
/// regularly; an epoch it has not seen clears its own in-memory copy, so a <c>data.changed</c> that reached one instance reaches them all within a poll. The dataset <see cref="ServingCacheEpochs.All"/> is a whole-cache clear.
/// </summary>
public interface IServingCacheEpochs
{
    /// <summary>Moves the dataset's epoch forward (never back) and records who and why; returns the new epoch.</summary>
    long Bump(string dataset, string product, string reason);

    /// <summary>Every dataset's current epoch.</summary>
    IReadOnlyDictionary<string, long> Read();
}

public static class ServingCacheEpochs
{
    /// <summary>The epoch name of a clear of the whole cache.</summary>
    public const string All = "*";

    /// <summary>The next epoch: later than the one before and than the clock (microseconds since 1970), so a row that was removed by retention and made again can never repeat a number an instance has seen.</summary>
    public static long Next(long previous, DateTimeOffset now) => Math.Max(previous + 1, now.ToUnixTimeMilliseconds() * 1000);
}

/// <summary>
/// Clears the cache when data changes (24a UC13) and keeps the instances of a group in step (UC14). <paramref name="resolve"/> maps the dataset a producer names to the datasets the cache keys its entries by (what the host serves that reads it,
/// by the lineage graph); null means the host cannot say, and the whole cache goes (safe, never stale). With <paramref name="epochs"/> the clear is also recorded in the group's shared state and <see cref="Poll"/> applies what the others recorded.
/// </summary>
public sealed class ServingCacheInvalidator(IServingCache cache, Func<string, IReadOnlyList<string>?> resolve, IServingCacheEpochs? epochs = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private long _received, _cleared, _manual;
    private DateTimeOffset? _lastAt, _lastManualAt;
    private string? _lastProduct, _lastDataset;
    private bool _started;

    public ServingCacheActivity Activity
    {
        get
        {
            lock (_gate) return new ServingCacheActivity(_received, _cleared, _lastAt, _lastProduct, _lastDataset, _manual, _lastManualAt);
        }
    }

    /// <summary>Whether the group's shared record is in use (more than one instance can share a change).</summary>
    public bool Shared => epochs is not null;

    /// <summary>Applies a message a producer sent: clears what reads the dataset here and tells the group.</summary>
    public DataChangedOutcome Apply(DataChangedNotice notice)
    {
        var datasets = resolve(notice.Dataset);
        var removed = 0;
        if (datasets is null)
        {
            removed = cache.Clear();
            Record(ServingCacheEpochs.All, notice.Product, "data.changed " + notice.Dataset);
        }
        else
            foreach (var dataset in datasets.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                removed += cache.Invalidate(dataset);
                Record(dataset, notice.Product, "data.changed " + notice.Dataset);
            }

        lock (_gate)
        {
            _received++;
            _cleared += removed;
            _lastAt = _time.GetUtcNow();
            _lastProduct = notice.Product;
            _lastDataset = notice.Dataset;
        }

        return new DataChangedOutcome(datasets, removed, datasets is null);
    }

    /// <summary>An administrator clears one cache dataset (as <see cref="IServingCache.Invalidate"/> names it), or everything when it is null; the group hears of it.</summary>
    public int ClearByHand(string? dataset, string who)
    {
        var removed = dataset is null ? cache.Clear() : cache.Invalidate(dataset);
        Record(dataset ?? ServingCacheEpochs.All, "console", "cleared by " + who);
        lock (_gate)
        {
            _manual++;
            _cleared += removed;
            _lastManualAt = _time.GetUtcNow();
        }

        return removed;
    }

    private void Record(string dataset, string product, string reason)
    {
        if (epochs is null) return;
        try
        {
            var epoch = epochs.Bump(dataset, product, reason);
            lock (_gate) _seen[dataset] = epoch;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the shared record is down: this instance is cleared, the others fall back to the time-to-live (a miss, never an error)
        }
    }

    /// <summary>
    /// Applies the epochs the other instances recorded since the last call. The first call only learns the current epochs (a cache that was just made is empty). A shared record that cannot be read changes nothing: the time-to-live is the backstop.
    /// Returns the number of entries removed.
    /// </summary>
    public int Poll()
    {
        if (epochs is null) return 0;
        IReadOnlyDictionary<string, long> now;
        try { now = epochs.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
        var removed = 0;
        List<string> changed = [];
        lock (_gate)
        {
            foreach (var (dataset, epoch) in now)
            {
                if (_seen.TryGetValue(dataset, out var known) && known == epoch) continue;
                _seen[dataset] = epoch;
                if (_started) changed.Add(dataset);
            }

            _started = true;
        }

        foreach (var dataset in changed) removed += dataset == ServingCacheEpochs.All ? cache.Clear() : cache.Invalidate(dataset);
        if (changed.Count > 0)
            lock (_gate) _cleared += removed;
        return removed;
    }
}
