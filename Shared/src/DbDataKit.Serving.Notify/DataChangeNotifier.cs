using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Serving.Notify;

/// <summary>A dataset a product finished writing: its lineage identity (<c>namespace/name</c>, what <c>LineageIdentity.Dataset</c> gives), the run, load or write that made it (for the audit) and when.</summary>
public sealed record DataChange(string Dataset, string? Run = null, DateTimeOffset? At = null);

/// <summary>What a notifier did since it started: how many messages a Gateway took (<see cref="Sent"/>), how many said they do not serve the dataset (<see cref="NotServed"/>, no failure), how many were tried again (<see cref="Retried"/>) and how many were given up on (<see cref="Failed"/>), and the last failure in words.</summary>
public sealed record DataChangeStats(long Sent, long NotServed, long Retried, long Failed, DateTimeOffset? LastSentAt, DateTimeOffset? LastFailureAt, string? LastFailure, int Pairings);

/// <summary>What a product calls when a run, load or write is over. It never throws and never waits for the network: a failure to tell a serving group is a log line and a counter, not a failed run.</summary>
public interface IDataChangeNotifier
{
    /// <summary>Tells every paired serving group that these datasets changed. With no pairing nothing is sent.</summary>
    void Publish(IEnumerable<DataChange> changes);

    DataChangeStats Stats { get; }
}

/// <summary>The notifier of a server that has none (a test, a tool): sends nothing.</summary>
public sealed class NoDataChangeNotifier : IDataChangeNotifier
{
    public static readonly NoDataChangeNotifier Instance = new();

    public void Publish(IEnumerable<DataChange> changes) { }

    public DataChangeStats Stats => new(0, 0, 0, 0, null, null, null, 0);
}

/// <summary>How a notifier behaves; every default is the one the plan names.</summary>
public sealed class DataChangeNotifierOptions
{
    /// <summary>How long before the one retry of a message that could not be delivered (a Gateway that was down, a network that dropped). A message is tried twice and then given up on, which the counter and the log say; the time-to-live of the cache is the backstop.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest one call waits for a Gateway.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The role of the token: <b>default taken</b> the existing Publisher role (it reads nothing and implies no other role), not a new one; the Gateway accepts <c>data.changed</c> from a Publisher or an Admin.</summary>
    public ServerRole Role { get; init; } = ServerRole.Publisher;
}

/// <summary>
/// The producer of <c>data.changed/1</c> (plan 24a UC13, QU8): a product server that finished loading, replicating, running or writing a dataset sends the message to every serving group it is paired with.
/// <para>
/// <b>Who signs.</b> A completion has no person to sign as, so the <b>server signs as itself</b>: the call carries a management token issued by the key of the server's own pairing with the Gateway (the same one it publishes with),
/// naming the server's client id as the subject, <c>&lt;product&gt; server</c> as the name and the role Publisher. There is no second signature: the Gateway's channel (token signature, audience, expiry and the replay cache shared by the group) refuses a forged or replayed
/// message before it is read, and its audit names the server.
/// </para>
/// <para>
/// <b>Which Gateway.</b> The message goes to every pairing with <c>only_if_served</c>, and a Gateway whose lineage graph does not know the dataset (it serves nothing that reads it) does nothing instead of clearing its cache. A product with no pairing sends nothing.
/// </para>
/// <para>
/// <b>Failure.</b> Delivery runs in the background. A call that fails for a reason that can pass (the Gateway down, a timeout, a 5xx) is tried once more after <see cref="DataChangeNotifierOptions.RetryAfter"/>; a refusal (a role, a body, an expired pairing) is not retried. Either way the
/// run that caused it is never changed: the failure is a warning in the log and the counters of <see cref="Stats"/>.
/// </para>
/// </summary>
public sealed class DataChangeNotifier : IDataChangeNotifier, IAsyncDisposable
{
    private readonly string _product;
    private readonly IGatewayPairings _pairings;
    private readonly Func<CancellationToken, Task<ConsoleIdentity>> _identity;
    private readonly ILogger? _log;
    private readonly DataChangeNotifierOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<GatewayPairing, HttpMessageHandler> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _running = [];
    private long _sent, _notServed, _retried, _failed;
    private DateTimeOffset? _lastSentAt, _lastFailureAt;
    private string? _lastFailure;
    private int _pairingCount;

    /// <param name="product">What the message says made the change: <c>build</c>, <c>sync</c>, <c>store</c>, <c>runner</c>.</param>
    /// <param name="pairings">The serving groups this server is paired with.</param>
    /// <param name="identity">This server's own identity (its key and client id), as a pairing was made with.</param>
    /// <param name="handler">Test hook: the handler of a call to a pairing; the default pins the pairing's certificate.</param>
    public DataChangeNotifier(string product, IGatewayPairings pairings, Func<CancellationToken, Task<ConsoleIdentity>> identity, ILogger? log = null,
        DataChangeNotifierOptions? options = null, TimeProvider? time = null, Func<GatewayPairing, HttpMessageHandler>? handler = null)
    {
        _product = product;
        _pairings = pairings;
        _identity = identity;
        _log = log;
        _options = options ?? new DataChangeNotifierOptions();
        _time = time ?? TimeProvider.System;
        _handler = handler ?? (p => PairingClient.Pinned(p.Fingerprint));
    }

    public DataChangeStats Stats
    {
        get
        {
            lock (_gate) return new DataChangeStats(_sent, _notServed, _retried, _failed, _lastSentAt, _lastFailureAt, _lastFailure, _pairingCount);
        }
    }

    public void Publish(IEnumerable<DataChange> changes)
    {
        try
        {
            var list = changes.Where(c => !string.IsNullOrWhiteSpace(c.Dataset)).DistinctBy(c => c.Dataset, StringComparer.Ordinal).ToList();
            if (list.Count == 0) return;
            IReadOnlyList<GatewayPairing> pairings;
            try { pairings = _pairings.All(); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                Fail($"The list of paired gateways could not be read ({ex.GetType().Name}), so no serving group was told of {list.Count} changed dataset(s).");
                return;
            }

            lock (_gate) _pairingCount = pairings.Count;
            foreach (var pairing in pairings)
                foreach (var change in list)
                    Start(pairing, change with { At = change.At ?? _time.GetUtcNow() });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a notifier that cannot even start must not take the run with it
            Fail($"The data change could not be sent ({ex.GetType().Name}).");
        }
    }

    private void Start(GatewayPairing pairing, DataChange change)
    {
        Task? task = null;
        task = Task.Run(async () =>
        {
            try { await DeliverAsync(pairing, change).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fail($"{pairing.Name}: the change of '{change.Dataset}' could not be sent ({ex.GetType().Name})."); }
            finally
            {
                lock (_gate) _running.Remove(task!);
            }
        });
        lock (_gate) _running.Add(task);
    }

    /// <summary>Waits until every message started so far has been delivered or given up on (a test, a clean stop). Never throws.</summary>
    public async Task FlushAsync(TimeSpan? timeout = null)
    {
        Task[] now;
        lock (_gate) now = [.. _running];
        try { await Task.WhenAll(now).WaitAsync(timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* what is still going on is left to finish or be stopped by Dispose */ }
    }

    private async Task DeliverAsync(GatewayPairing pairing, DataChange change)
    {
        var first = await TryOnceAsync(pairing, change).ConfigureAwait(false);
        if (first.Outcome != Outcome.Retry) { Record(first, pairing, change); return; }
        lock (_gate) _retried++;
        _log?.LogInformation("The serving group '{Gateway}' did not take the change of '{Dataset}' ({Why}); trying again in {Seconds} s.", pairing.Name, change.Dataset, first.Why, (int)_options.RetryAfter.TotalSeconds);
        try { await Task.Delay(_options.RetryAfter, _time, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Fail($"{pairing.Name}: the change of '{change.Dataset}' was not sent: {first.Why}; the server stopped before the retry."); return; }
        Record(await TryOnceAsync(pairing, change).ConfigureAwait(false), pairing, change);
    }

    private enum Outcome { Sent, NotServed, Retry, Refused }

    private readonly record struct Attempt(Outcome Outcome, string Why);

    private void Record(Attempt attempt, GatewayPairing pairing, DataChange change)
    {
        switch (attempt.Outcome)
        {
            case Outcome.Sent:
                lock (_gate) { _sent++; _lastSentAt = _time.GetUtcNow(); }
                break;
            case Outcome.NotServed:
                lock (_gate) _notServed++;
                break;
            default:
                Fail($"{pairing.Name}: the change of '{change.Dataset}' was not delivered: {attempt.Why}. The cache of that group clears by its time-to-live.");
                break;
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _failed++;
            _lastFailureAt = _time.GetUtcNow();
            _lastFailure = message;
        }

        _log?.LogWarning("{Message}", message);
    }

    private async Task<Attempt> TryOnceAsync(GatewayPairing pairing, DataChange change)
    {
        try
        {
            var self = await _identity(_stop.Token).ConfigureAwait(false);
            using var http = new HttpClient(_handler(pairing)) { BaseAddress = new Uri(pairing.Url), Timeout = _options.CallTimeout };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cache/changed");
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(pairing.InstanceId, self.Id, _product + " server", _options.Role)));
            var notice = new DataChangedNotice(change.Dataset, _product, change.At ?? _time.GetUtcNow(), change.Run, OnlyIfServed: true);
            request.Content = new StringContent(DataChangedMessage.Write(notice), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
            using var response = await http.SendAsync(request, _stop.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(_stop.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var served = true;
                try { served = (JsonNode.Parse(text) as JsonObject)?["served"]?.GetValue<bool>() ?? true; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { /* an answer that is not the shape we know still means it was taken */ }
                return new Attempt(served ? Outcome.Sent : Outcome.NotServed, "");
            }

            var detail = "";
            try { detail = (string?)(JsonNode.Parse(text) as JsonObject)?["error"] ?? ""; } catch (System.Text.Json.JsonException) { }
            var why = $"it answered {(int)response.StatusCode}{(detail.Length > 0 ? " " + detail : "")}";
            // a 5xx, a 429 and a 408 can pass; a refusal (a role, a pairing that expired, a body) will not by waiting
            return new Attempt(response.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ? Outcome.Retry : Outcome.Refused, why);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return new Attempt(Outcome.Refused, "the server stopped");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException or OperationCanceledException)
        {
            return new Attempt(Outcome.Retry, $"it could not be reached ({ex.GetType().Name})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
