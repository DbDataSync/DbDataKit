using System.Text.Json.Nodes;

namespace DbDataKit.Telemetry;

/// <summary>The <c>telemetry:</c> section is wrong; the message names the file and the key.</summary>
public sealed class TelemetrySettingsException(string message) : Exception(message);

/// <summary>
/// The <c>telemetry:</c> settings section (plan 22a GP27), read strictly: an unknown key fails naming the file and the key. <b>Off by default</b>: without <c>enabled: true</c> nothing is created, no exporter and no listener on any <see cref="System.Diagnostics.ActivitySource"/>.
/// <code>
/// telemetry:
///   enabled: true
///   endpoint: http://collector.internal:4317     # the OTLP endpoint; the exporter's own default when unset
///   protocol: grpc                                # grpc | http
///   sampling_ratio: 1.0                           # 0 to 1, the share of traces kept
///   instance_id: console-1                        # service.instance.id; the machine name when unset
/// </code>
/// </summary>
public sealed record TelemetrySettings(bool Enabled = false, Uri? Endpoint = null, string Protocol = "grpc", double SamplingRatio = 1.0, string? InstanceId = null)
{
    public static TelemetrySettings Off { get; } = new();

    private static readonly string[] Keys = ["enabled", "endpoint", "protocol", "sampling_ratio", "instance_id"];

    /// <summary>Reads the section; <paramref name="source"/> is the file it came from, named in every refusal. A null section is <see cref="Off"/>.</summary>
    public static TelemetrySettings Read(JsonObject? section, string source)
    {
        if (section is null) return Off;
        foreach (var (key, _) in section)
            if (!Keys.Contains(key)) throw new TelemetrySettingsException($"{source}: telemetry has no key `{key}`. Keys: {string.Join(", ", Keys)}.");
        var enabled = false;
        if (section["enabled"] is { } e)
            enabled = e is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new TelemetrySettingsException($"{source}: telemetry.enabled is true or false.");
        Uri? endpoint = null;
        if (section["endpoint"] is { } ep)
        {
            var text = ep is JsonValue ev && ev.TryGetValue<string>(out var s) ? s : null;
            if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out endpoint) || endpoint.Scheme is not ("http" or "https"))
                throw new TelemetrySettingsException($"{source}: telemetry.endpoint is an http or https address, like http://collector.internal:4317.");
            if (!string.IsNullOrEmpty(endpoint.UserInfo)) throw new TelemetrySettingsException($"{source}: telemetry.endpoint carries a login in its address; an address holds no secret.");
        }

        var protocol = "grpc";
        if (section["protocol"] is { } p)
            protocol = p is JsonValue pv && pv.TryGetValue<string>(out var ps) && ps is "grpc" or "http" ? ps : throw new TelemetrySettingsException($"{source}: telemetry.protocol is `grpc` or `http`.");
        var ratio = 1.0;
        if (section["sampling_ratio"] is { } r)
            ratio = r is JsonValue rv && rv.TryGetValue<double>(out var d) && d is >= 0 and <= 1 ? d : throw new TelemetrySettingsException($"{source}: telemetry.sampling_ratio is a number from 0 to 1.");
        string? instance = null;
        if (section["instance_id"] is { } i)
            instance = i is JsonValue iv && iv.TryGetValue<string>(out var istr) && istr.Length is > 0 and <= 128 ? istr : throw new TelemetrySettingsException($"{source}: telemetry.instance_id is a name of 1 to 128 characters.");
        return new TelemetrySettings(enabled, endpoint, protocol, ratio, instance);
    }
}
