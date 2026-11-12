using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DbDataKit.Telemetry;

/// <summary>
/// For tests (plan 22a GP28, GP29): sees what a host exports without a collector. <see cref="Start"/> makes the capture current for the code that follows (and the tasks it starts); a host built after that, with <c>telemetry: enabled: true</c>,
/// adds an in-memory exporter that feeds this capture. The instruments are static, so two hosts in one process each see the other's spans: filter by <see cref="HostTelemetry.ProductTag"/>.
/// </summary>
public sealed class TelemetryCapture : IDisposable
{
    private static readonly AsyncLocal<TelemetryCapture?> CurrentCapture = new();
    private readonly List<Metric> _metrics = [];
    private readonly List<Activity> _spans = [];

    private TelemetryCapture()
    {
    }

    internal static TelemetryCapture? Current => CurrentCapture.Value;

    /// <summary>Starts capturing for the code that runs after this call; dispose it when the test ends.</summary>
    public static TelemetryCapture Start() => CurrentCapture.Value = new TelemetryCapture();

    internal void ConfigureTracing(TracerProviderBuilder tracing) => tracing.AddInMemoryExporter(_spans);

    internal void ConfigureMetrics(MeterProviderBuilder metrics) => metrics.AddInMemoryExporter(_metrics, o => o.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 3_600_000);

    /// <summary>Exports what is pending from the host <paramref name="services"/> belong to (the metrics go out on a timer otherwise).</summary>
    public void Flush(IServiceProvider services)
    {
        services.GetService<TracerProvider>()?.ForceFlush();
        services.GetService<MeterProvider>()?.ForceFlush();
    }

    /// <summary>The spans exported so far.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_spans) return [.. _spans];
        }
    }

    /// <summary>Every attribute and label that was exported, as <c>key=value</c> text: spans' tags and metrics' labels, for a test that scans for something that must not be there.</summary>
    public IReadOnlyList<string> AllAttributes()
    {
        var all = new List<string>();
        foreach (var span in Spans)
        {
            all.Add("name=" + span.DisplayName);
            all.AddRange(span.TagObjects.Select(t => $"{t.Key}={t.Value}"));
            all.AddRange(span.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}")));
            all.AddRange(span.Baggage.Select(b => $"{b.Key}={b.Value}"));
        }

        foreach (var metric in Latest())
        {
            all.Add("metric=" + metric.Name);
            foreach (var point in metric.GetMetricPoints())
                foreach (var tag in point.Tags) all.Add($"{tag.Key}={tag.Value}");
        }

        return all;
    }

    /// <summary>The sum of a counter's points whose labels all match <paramref name="labels"/> (the last export of that counter).</summary>
    public long Sum(string counter, params (string Key, string Value)[] labels)
    {
        long total = 0;
        foreach (var metric in Latest().Where(m => m.Name == counter))
            foreach (var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, string?>();
                foreach (var tag in point.Tags) tags[tag.Key] = tag.Value?.ToString();
                if (labels.All(l => tags.TryGetValue(l.Key, out var v) && v == l.Value)) total += metric.MetricType == MetricType.LongSum ? point.GetSumLong() : (long)point.GetHistogramCount();
            }

        return total;
    }

    /// <summary>The number of recordings of a histogram whose labels match.</summary>
    public long Count(string histogram, params (string Key, string Value)[] labels) => Sum(histogram, labels);

    private IEnumerable<Metric> Latest()
    {
        lock (_metrics) return _metrics.GroupBy(m => m.Name).Select(g => g.Last()).ToList();
    }

    public void Dispose()
    {
        if (CurrentCapture.Value == this) CurrentCapture.Value = null;
    }
}
