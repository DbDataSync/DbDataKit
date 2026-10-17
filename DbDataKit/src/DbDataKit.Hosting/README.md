# DbDataKit.Hosting

ASP.NET Core glue over `DbDataKit.Hosting.Abstractions`. Depends on the shared framework plus `Microsoft.Extensions.Hosting.WindowsServices` and `.Systemd`.

```csharp
var mode = builder.UseDbDataHost(o => { o.Product = "DbDataProxy"; o.Version = v; o.Commit = c; o.SchemaVersions["config"] = "3"; });
var app = builder.Build();
app.UseDbDataPipeline();   // forwarded headers (only if configured), timeouts, concurrency cap, drain
app.MapDbDataEndpoints();  // /health, /health/live, /health/ready, /version
```

Decisions: Windows service / systemd integration is enabled only when detected (and never with `--console`); `ApplicationStopping` blocks (bounded by `DrainTimeout`) while in-flight requests finish and `HostOptions.ShutdownTimeout` is raised to cover it; `/health` and `/version` are exempt from drain and the concurrency cap so the updater can poll through a drain; register `IReadinessProbe` services for component checks. Logging/redactor wiring (Plan 02) is left to the product for now.
