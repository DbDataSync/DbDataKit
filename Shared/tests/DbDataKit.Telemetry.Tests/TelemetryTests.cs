using System.Diagnostics;
using System.Text.Json.Nodes;
using DbDataKit.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace DbDataKit.Telemetry.Tests;

/// <summary>Plan 22a GP27: the telemetry: section, off by default, and the SDK behind one call.</summary>
public sealed class TelemetryTests
{
    private static TelemetrySettings Read(string json) => TelemetrySettings.Read(JsonNode.Parse(json)!.AsObject(), "settings.yaml");

    [Fact]
    public void The_section_is_read_strictly_and_nothing_is_on_by_default()
    {
        Assert.False(TelemetrySettings.Read(null, "settings.yaml").Enabled);
        Assert.False(Read("{}").Enabled);
        var on = Read("""{"enabled": true, "endpoint": "http://collector:4317", "protocol": "http", "sampling_ratio": 0.25, "instance_id": "console-1"}""");
        Assert.Equal((true, "http", 0.25, "console-1"), (on.Enabled, on.Protocol, on.SamplingRatio, on.InstanceId));
        Assert.Equal("http://collector:4317/", on.Endpoint!.AbsoluteUri);

        static string Refused(string json) => Assert.Throws<TelemetrySettingsException>(() => Read(json)).Message;
        Assert.Contains("settings.yaml: telemetry has no key `exporter`", Refused("""{"exporter": "otlp"}"""));
        Assert.Contains("telemetry.enabled is true or false", Refused("""{"enabled": "yes"}"""));
        Assert.Contains("telemetry.endpoint is an http or https address", Refused("""{"endpoint": "collector"}"""));
        Assert.Contains("carries a login", Refused("""{"endpoint": "http://user:pw@collector:4317"}"""));
        Assert.Contains("telemetry.protocol is `grpc` or `http`", Refused("""{"protocol": "udp"}"""));
        Assert.Contains("telemetry.sampling_ratio is a number from 0 to 1", Refused("""{"sampling_ratio": 2}"""));
    }

    [Fact]
    public void The_section_is_loaded_from_a_settings_file_and_a_bad_file_is_named()
    {
        var directory = Directory.CreateTempSubdirectory("telemetry-load-").FullName;
        try
        {
            var file = Path.Combine(directory, "server.yaml");
            Assert.False(TelemetrySettings.Load(file).Enabled);                        // no file
            File.WriteAllText(file, "listen: https://localhost:1\n");
            Assert.False(TelemetrySettings.Load(file).Enabled);                        // no section
            File.WriteAllText(file, "listen: https://localhost:1\ntelemetry:\n  enabled: true\n  endpoint: http://collector:4317\n  protocol: http\n  sampling_ratio: 0.5\n  instance_id: \"console-1\"\n");
            var on = TelemetrySettings.Load(file);
            Assert.Equal((true, "http", 0.5, "console-1"), (on.Enabled, on.Protocol, on.SamplingRatio, on.InstanceId));
            File.WriteAllText(file, "telemetry:\n  enabled: true\n  exporter: otlp\n");
            Assert.Contains(file + ": telemetry has no key `exporter`", Assert.Throws<TelemetrySettingsException>(() => TelemetrySettings.Load(file)).Message);
            File.WriteAllText(file, "telemetry: yes\n");
            Assert.Contains(file, Assert.Throws<TelemetrySettingsException>(() => TelemetrySettings.Load(file)).Message);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Off_creates_no_exporter_and_no_listener_on_any_source()
    {
        var services = new ServiceCollection();
        SuiteTelemetry.Add(services, TelemetrySettings.Off, "dbdatakit", "1.0");
        Assert.Empty(services);                                              // nothing registered: no provider, no exporter
        using var provider = services.BuildServiceProvider();
        Assert.False(SuiteTelemetry.Source.HasListeners());
        using var span = SuiteTelemetry.Source.StartActivity("off");
        Assert.Null(span);                                                    // no listener: no activity is made
    }

    [Fact]
    public async Task On_an_in_memory_exporter_receives_a_span_and_a_metric_with_the_resource_attributes()
    {
        var spans = new List<Activity>();
        var metrics = new List<Metric>();
        var services = new ServiceCollection();
        services.AddLogging();
        SuiteTelemetry.Add(services, new TelemetrySettings(Enabled: true, Endpoint: new Uri("http://127.0.0.1:1"), InstanceId: "test-1"), "dbdatakit-test", "9.9.9",
            t => t.AddInMemoryExporter(spans), m => m.AddInMemoryExporter(metrics, o => o.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 100_000));
        using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>()) await hosted.StartAsync(default);

        using (var span = SuiteTelemetry.Source.StartActivity("a request"))
        {
            Assert.NotNull(span);
            span!.SetTag("route", "/api/x");
        }

        SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.test.requests").Add(3);
        provider.GetRequiredService<TracerProvider>().ForceFlush();
        provider.GetRequiredService<MeterProvider>().ForceFlush();

        var recorded = Assert.Single(spans, s => s.OperationName == "a request");
        Assert.Equal("/api/x", recorded.GetTagItem("route"));
        Assert.Contains(metrics, m => m.Name == "dbdatakit.test.requests");

        var resource = provider.GetRequiredService<TracerProvider>().GetResource();
        var attributes = resource.Attributes.ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(("dbdatakit-test", "9.9.9", "test-1"), ((string)attributes["service.name"], (string)attributes["service.version"], (string)attributes["service.instance.id"]));
        foreach (var hosted in provider.GetServices<IHostedService>()) await hosted.StopAsync(default);
    }
}
