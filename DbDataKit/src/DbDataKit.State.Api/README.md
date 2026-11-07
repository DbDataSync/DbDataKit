# DbDataKit.State.Api

The state endpoints every server product's management API has (plan 29b, C05), so a console can see a server's state and prepare a move of it (`state move`, C04; the screens, C06).

It is a project of its own because it needs `DbDataKit.State.Cli` (the read-only inspection `state show` uses) and so `DbDataKit.State.Engines` and its two database providers: `DbDataKit.Web` must not reference those drivers, and every server product already references `State.Cli`. The product registers it with `<DbDataKitReference Include="State.Api" />`.

* `GET /api/v1/state` (viewer): product, software version, management API version, engine, connection (secret references as written, never resolved; the SQLite file), the registry table, where each effective setting came from (`sources`: flag, environment, file, default), each module with its place (schema, prefix, file), the version in the binary, the version in the registry and what is pending, `pending_migrations`, `instance` (this instance's module versions), `instances` (the other live instances, from the product's `IInstanceDirectory`; empty when the product keeps no instance table), and `maintenance`. Read-only: it opens nothing for writing, creates and migrates nothing. A state that cannot be read is still answered (`read: false`, `error` names the fix). The last answer step replaces any literal credential of the connection string. Last export and verify times are left out: nothing records them.
* `GET /api/v1/state/maintenance` (viewer): `enabled`, `idle`, `since`, `reason`, `inflight`.
* `POST /api/v1/state/maintenance` (admin), body `{"enabled": true, "reason": "moving the state"}`: the reason is needed to enter. While it is on, `MaintenanceMiddleware.UseMaintenance` answers every request with 503, `Retry-After: 30` and `{"error", "maintenance": true, "reason", "since", "retry_after_seconds"}`, except the state endpoints, `/health*`, `/version`, `/management/` (pairing) and requests of an administrator. Requests are counted while answered, and `idle` is true when none is left and the product's own work (`MaintenanceGate`'s `work`, such as plans a scheduler runs) is done. Nothing is persisted: a restart leaves maintenance (a move needs the restart anyway).
* Every change is audited as `state.maintenance` (who, through which console, the reason; never a secret) through the product's `IAuditLog`, and a refusal (a role that is not enough, a body that is not valid) too. A change that cannot be audited is not kept.

## What a product does

```csharp
var gate = new MaintenanceGate(onEnter: reason => /* stop claiming */, onLeave: () => /* claim again */, work: () => running);
app.UseMaintenance(gate, context => /* is this request an administrator's */);   // after the authentication it reads
app.MapState(new StateManagement(new StateManagementOptions(product, program, "the server", () => stateTarget, modules, scope, gate, audit, instanceId, version, instances)));
```

`MapState` reads the console's token that `UseManagementAuthentication` validated; a product that signs people in (the console) passes a function that makes a `StateCaller` from the session. A product with its own dispatcher (the Proxy, whose local channel shares it) calls `StateManagement.HandleAsync`. The audit is `StateAuditSink` where the state has the `audit` module (console, Proxy), else a `FileAuditLog` (Store, Build). `IInstanceDirectory` is the seam for the instances sharing a state: the Proxy's `serving_instance` (which records no versions, so they are named without them); the default is empty.
