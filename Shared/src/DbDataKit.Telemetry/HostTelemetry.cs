using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using OpenTelemetry;

namespace DbDataKit.Telemetry;

/// <summary>
/// The instruments the hosts share (plan 22a GP28): requests, runs, state commands and task starts. Every label is a name from a short list the code writes (a product, a route template, a kind, an outcome, a statement's name): <b>never a
/// secret, a statement's text, a parameter, a token or a row value</b>. A name that is not a plain identifier is reported as <c>unnamed</c> (<see cref="Name"/>), so text cannot slip in as a name. With telemetry off nothing listens and every call is a no-op.
/// </summary>
public static class HostTelemetry
{
    public const string ProductTag = "dbdatakit.product";
    public const string KindTag = "dbdatakit.run.kind";
    public const string OutcomeTag = "dbdatakit.outcome";
    public const string NameTag = "dbdatakit.name";

    public static Counter<long> Requests { get; } = SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.http.server.requests", "{request}", "Requests a host answered, by method, route template and status code.");
    public static Histogram<double> RequestDuration { get; } = SuiteTelemetry.Meter.CreateHistogram<double>("dbdatakit.http.server.duration", "ms", "How long a host took to answer.");
    public static Counter<long> Runs { get; } = SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.runs", "{run}", "Runs that ended, by kind and outcome (succeeded, failed, cancelled).");
    public static Histogram<double> RunDuration { get; } = SuiteTelemetry.Meter.CreateHistogram<double>("dbdatakit.run.duration", "ms", "How long a run took.");
    public static Counter<long> RunStarts { get; } = SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.run.starts", "{run}", "Runs that started, by kind.");
    public static Counter<long> TaskStarts { get; } = SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.runner.task_starts", "{task}", "Tasks the runner started, by task name.");

    private static readonly Regex Plain = new("^[A-Za-z0-9_.:/-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A name that may be a label or an attribute value: a short identifier. Anything else (a space, a quote, a parenthesis: SQL text, a path with a secret in it) becomes <c>unnamed</c>.</summary>
    public static string Name(string? value) => value is not null && Plain.IsMatch(value) ? value : "unnamed";

    /// <summary>A run of work (a replication run, a build, a task) that counts when it starts and when it ends and is one span; end it with <see cref="RunScope.Succeeded"/>, <see cref="RunScope.Failed"/> or <see cref="RunScope.Cancelled"/>.</summary>
    public static RunScope StartRun(string product, string kind, string? name = null)
    {
        var tags = new TagList { { ProductTag, Name(product) }, { KindTag, Name(kind) } };
        RunStarts.Add(1, tags);
        var span = SuiteTelemetry.Source.StartActivity("run " + Name(kind), ActivityKind.Internal);
        span?.SetTag(ProductTag, Name(product));
        span?.SetTag(KindTag, Name(kind));
        if (name is not null) span?.SetTag(NameTag, Name(name));
        return new RunScope(product, kind, span);
    }

    /// <summary>A run that a host only observes (Sync sees a worker's runs in its state database): counted when first seen.</summary>
    public static void RunStarted(string product, string kind) => RunStarts.Add(1, new TagList { { ProductTag, Name(product) }, { KindTag, Name(kind) } });

    /// <summary>A run that a host only observes ended: counted by outcome (<c>succeeded</c>, <c>failed</c>, <c>cancelled</c>), with its duration when known.</summary>
    public static void RunEnded(string product, string kind, string outcome, TimeSpan? duration = null)
    {
        var tags = new TagList { { ProductTag, Name(product) }, { KindTag, Name(kind) }, { OutcomeTag, outcome is "succeeded" or "failed" or "cancelled" ? outcome : "failed" } };
        Runs.Add(1, tags);
        if (duration is { } d) RunDuration.Record(d.TotalMilliseconds, tags);
    }

    /// <summary>The runner starts a task: counted by the task's name.</summary>
    public static void TaskStarted(string product, string task) => TaskStarts.Add(1, new TagList { { ProductTag, Name(product) }, { NameTag, Name(task) } });

    /// <summary>Adds the current activity's W3C <c>traceparent</c> (and <c>tracestate</c>) to an outgoing call so the server at the other end continues the trace. Nothing is added when there is no current activity (telemetry off).</summary>
    public static void Inject(HttpRequestHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (Activity.Current is not { IdFormat: ActivityIdFormat.W3C } current || headers.Contains("traceparent")) return;
        headers.TryAddWithoutValidation("traceparent", current.Id);
        if (!string.IsNullOrEmpty(current.TraceStateString)) headers.TryAddWithoutValidation("tracestate", current.TraceStateString);
    }
}

/// <summary>One run in progress (<see cref="HostTelemetry.StartRun"/>). Disposed without an outcome it counts as failed.</summary>
public sealed class RunScope : IDisposable
{
    private readonly string _product;
    private readonly string _kind;
    private readonly Activity? _span;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _done;

    internal RunScope(string product, string kind, Activity? span)
    {
        _product = product;
        _kind = kind;
        _span = span;
    }

    public void Succeeded() => End("succeeded", null);

    public void Cancelled() => End("cancelled", null);

    /// <summary>The run failed; only the exception's type is kept, never its message (a message can hold text, a path or a value).</summary>
    public void Failed(Exception? error = null) => End("failed", error);

    private void End(string outcome, Exception? error)
    {
        if (_done) return;
        _done = true;
        var tags = new TagList { { HostTelemetry.ProductTag, HostTelemetry.Name(_product) }, { HostTelemetry.KindTag, HostTelemetry.Name(_kind) }, { HostTelemetry.OutcomeTag, outcome } };
        HostTelemetry.Runs.Add(1, tags);
        HostTelemetry.RunDuration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tags);
        if (_span is null) return;
        _span.SetTag(HostTelemetry.OutcomeTag, outcome);
        if (outcome == "failed") _span.SetStatus(ActivityStatusCode.Error);
        if (error is not null) _span.SetTag("error.type", error.GetType().FullName);
        _span.Dispose();
    }

    public void Dispose() => End("failed", null);
}

/// <summary>
/// The last guard on what leaves the process: before a span is exported, every attribute whose key names a secret, a token, a query, a statement, a parameter or a body is removed, whoever set it. The code of the suite sets no such attribute;
/// this keeps one added later from leaking (GP29 scrub test).
/// </summary>
internal sealed partial class AttributeScrubProcessor : BaseProcessor<Activity>
{
    [GeneratedRegex("token|secret|passw|authorization|bearer|credential|api[_.-]?key|cookie|query|sql|statement|param|body|payload|value|url\\.full|url\\.path|connection[_.-]?string", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Denied();

    public override void OnEnd(Activity data)
    {
        List<string>? remove = null;
        foreach (var tag in data.TagObjects)
            if (Denied().IsMatch(tag.Key)) (remove ??= []).Add(tag.Key);
        if (remove is null) return;
        foreach (var key in remove) data.SetTag(key, null);
    }
}
