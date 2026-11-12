using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DbDataKit.Telemetry;

/// <summary>
/// The suite's instruments and the one call that turns the SDK on (plan 22a GP27). Code creates spans and counters from <see cref="Source"/> and <see cref="Meter"/> whether or not telemetry is on: with it off they have no listener and cost nothing.
/// A host calls <see cref="Add"/> once; the resource says which product, instance and version a span or metric came from. The exporter is OTLP (<see cref="TelemetrySettings.Protocol"/>); the instrument names a host adds are its own,
/// and <b>nothing secret or parametric goes in a span or a tag</b> (a statement's name, never its text or parameters; GP28, GP29).
/// </summary>
public static class SuiteTelemetry
{
    /// <summary>The name of the suite's <see cref="ActivitySource"/> and <see cref="Meter"/>; sub-sources are named <c>DbDataKit.&lt;area&gt;</c>.</summary>
    public const string Name = "DbDataKit";

    public static ActivitySource Source { get; } = new(Name);

    public static Meter Meter { get; } = new(Name);

    /// <summary>The resource attributes a host reports: product, instance id and version.</summary>
    public static ResourceBuilder Resource(string product, string version, string? instanceId) =>
        ResourceBuilder.CreateEmpty().AddService(serviceName: product, serviceVersion: version, serviceInstanceId: instanceId ?? Environment.MachineName);

    /// <summary>
    /// Turns the SDK on for a host when <paramref name="settings"/> say so; with telemetry off it adds nothing to the services (no exporter, no listener on any source, asserted by a test). <paramref name="configureTracing"/> and
    /// <paramref name="configureMetrics"/> let a test add an in-memory exporter.
    /// </summary>
    public static IServiceCollection Add(IServiceCollection services, TelemetrySettings settings, string product, string version, Action<TracerProviderBuilder>? configureTracing = null, Action<MeterProviderBuilder>? configureMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled) return services;
        var resource = Resource(product, version, settings.InstanceId);
        var capture = TelemetryCapture.Current;   // a test that asked to see what the host exports (TelemetryCapture.Start)
        if (capture is not null) configureTracing ??= capture.ConfigureTracing;
        if (capture is not null) configureMetrics ??= capture.ConfigureMetrics;
        services.AddOpenTelemetry()
            .WithTracing(t =>
            {
                t.SetResourceBuilder(resource).AddProcessor(new AttributeScrubProcessor()).AddSource(Name, Name + ".*").SetSampler(new TraceIdRatioBasedSampler(settings.SamplingRatio)).AddOtlpExporter(o => Configure(o, settings, "v1/traces"));
                configureTracing?.Invoke(t);
            })
            .WithMetrics(m =>
            {
                m.SetResourceBuilder(resource).AddMeter(Name, Name + ".*").AddOtlpExporter(o => Configure(o, settings, "v1/metrics"));
                configureMetrics?.Invoke(m);
            });
        return services;
    }

    /// <summary>OTLP over HTTP takes the signal's own path when the endpoint is set in code (the SDK adds it only to the environment's endpoint).</summary>
    private static void Configure(OtlpExporterOptions options, TelemetrySettings settings, string httpPath)
    {
        options.Protocol = settings.Protocol == "http" ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        if (settings.Endpoint is { } endpoint) options.Endpoint = settings.Protocol == "http" ? new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/" + httpPath) : endpoint;
    }
}
