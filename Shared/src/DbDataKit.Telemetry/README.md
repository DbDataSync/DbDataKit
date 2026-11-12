# DbDataKit.Telemetry

OpenTelemetry for the suite's hosts (plan 22a GP27): one `telemetry:` settings section, off by default, the OTLP exporter and the resource attributes behind one call.

```yaml
telemetry:
  enabled: true                              # off unless true
  endpoint: http://collector.internal:4317   # OTLP; the exporter's own default when unset
  protocol: grpc                             # grpc | http (http adds /v1/traces and /v1/metrics to the endpoint)
  sampling_ratio: 1.0                        # 0 to 1: the share of traces kept
  instance_id: console-1                     # service.instance.id; the machine name when unset
```

```csharp
var settings = TelemetrySettings.Read(section, "settings.yaml");              // strict: an unknown key fails naming the file and the key
SuiteTelemetry.Add(services, settings, "dbdatakit", version);                  // nothing is registered when it is off
using var span = SuiteTelemetry.Source.StartActivity("state.migrate");         // the suite's ActivitySource; null when nothing listens
SuiteTelemetry.Meter.CreateCounter<long>("dbdatakit.runs").Add(1);
```

- **Off creates nothing.** Without `enabled: true` no provider, exporter or listener exists; `SuiteTelemetry.Source` has no listener and `StartActivity` returns null, so code may create spans unconditionally (asserted by a test).
- **Resource.** `service.name` is the product, `service.version` its version, `service.instance.id` the instance.
- **Sources.** The suite's own are `DbDataKit` and `DbDataKit.<area>`; only those are exported (ASP.NET Core's and the HTTP client's instrumentation are added by the host slices GP28 and GP29, not here).
- **Never in a span:** a secret, a statement's text or a parameter value. A statement's name is fine. GP29 adds the scrub test over every recorded attribute.
