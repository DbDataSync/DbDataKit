# Observability: traces and metrics

Every server of the suite (the console, Build, Sync, the Gateway, Store, Runner and Workspace) can send OpenTelemetry traces and metrics to a collector. It is **off by default**: with no `telemetry:` section, or `enabled: false`, nothing is created, no exporter, no listener on any source, no middleware (a test asserts it for the shared host and for each product). The library is `DbDataKit.Telemetry` (plan 22a GP27, GP28, GP29).

## The setting

```yaml
telemetry:
  enabled: true                              # off unless true
  endpoint: http://collector.internal:4317   # OTLP; the exporter's own default when unset
  protocol: grpc                             # grpc | http
  sampling_ratio: 1.0                        # 0 to 1: the share of traces kept
  instance_id: console-1                     # service.instance.id; the machine name when unset
```

It is a section of each server's settings file: `server.yaml` of Store, Runner, Workspace, Build and Sync; `settings.yaml` of the console; `server.yaml` of the Gateway, where the keys are spelled like the rest of that file (`enabled`, `endpoint`, `protocol`, `samplingRatio`, `instanceId`) and the Settings screen edits `enabled`, `endpoint` and `protocol`. An unknown key, a wrong type, a login in the address and a ratio outside 0 to 1 stop the server at start, naming the file and the key. A server reads it when it starts.

## What a server reports

| What | Name | Labels |
| --- | --- | --- |
| A request: one span per call, named `METHOD route-template` (`POST /api/v1/projects/{id}/commands/{tool}`), and a counter and a duration histogram | `dbdatakit.http.server.requests`, `dbdatakit.http.server.duration` | `http.request.method`, `http.route` (the template, never the path or the query string), `http.response.status_code`, `dbdatakit.product` |
| A run (a Sync run seen by the server, a Build scheduled run, a Runner task): a counter when it starts, a counter and a duration when it ends, and a span for the ones the host runs itself | `dbdatakit.run.starts`, `dbdatakit.runs`, `dbdatakit.run.duration` | `dbdatakit.product`, `dbdatakit.run.kind`, `dbdatakit.outcome` (`succeeded`, `failed`, `cancelled`) |
| A task the runner starts | `dbdatakit.runner.task_starts` | `dbdatakit.product`, `dbdatakit.name` (the task's name) |
| A state command (every statement a server sends to its state database) | `dbdatakit.state.commands` | `statement` (the **name**: the module and the verb, `runs.select`), `engine` |

Health and version calls are not traced. A failed request or run has the exception's **type** on its span (`error.type`), never its message.

## What is never reported

No secret, token, password, statement text, parameter, row value or path with a name in it is ever a span attribute or a metric label. Three rules keep it so:

1. The code sets only the attributes above, each from a short list it writes; a name that is not a plain identifier (no space, quote or bracket, at most 64 characters) is reported as `unnamed`, so text cannot pass as a name.
2. A processor removes, before export, any attribute whose key names a token, secret, password, authorization, cookie, query, statement, parameter, body, payload, value, full URL or connection string, whoever set it.
3. Tests send a bearer token, a signed management token, a query string, a query text and a row value through each host and scan every exported span and label for them (`TelemetryHostTests` and one test per product).

## One trace across servers

A request that the console relays to a paired server is one trace. The relay adds the W3C `traceparent` (and `tracestate`) of the console's own request span to the call it makes, and each server continues a trace whose header it receives. A caller's own `traceparent` on the request to the console is the root of both spans. The test is `TraceAcrossServersTests` (a real console and a real Store server in one process, both with in-memory exporters): the two spans have one trace id and the server's is a child of the console's.

Not built yet: the trace id in the serving contract, and a hop through a module (a query from an app through the semantic module to a database as one trace).

## For tests

`TelemetryCapture.Start()` makes the hosts built after it (in the same test) export to in-memory lists; `Flush(services)` exports what is pending, `Spans`, `Sum` and `AllAttributes` read it. The instruments are static, so two hosts in one process see each other's spans: filter by `dbdatakit.product`.
