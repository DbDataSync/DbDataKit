# DbDataKit.Delivery.Runner

The runner's half of scheduled delivery (plan 32 DBR18, plan 22a GP17): the system package **`views-export`** and its task **`subscriptions`**. The library `DbDataKit.Delivery` has the outbox, the channels and the subscriptions; this one is the task that runs them.

- **What it does.** Every minute (`ViewsExportSettings.Every`) the task takes the subscriptions that are due (`SubscriptionStore.ClaimDue`: each by a guarded update, so two runners never take one subscription for one occurrence) and, for each, has the document drawn **as the subscriber** through `ISubscriptionRenderer` (the views module's export under the subscriber's own principal: `DbDataKit.Views.Hosting`'s `SubscriptionRenderer`), then hands the PDF, CSV or xlsx to the delivery service as a payload. The input `subscription` (a subscription's id) sends that one now, whatever its schedule says; the schedule does not move.
- **Whose permissions.** The subscriber's: their roles and attributes decide the rows, and the audit row of the export names them. The runner's own identity is never used to read data. A subscriber the host cannot act as, or whom the document refuses, gets nothing: the subscription stops and the person and the owner are told (`SubscriptionService`).
- **How a run ends.** `succeeded` when every subscription taken sent or stopped for lost access (a result, not a failure); `completed with failures` when some could not render or had no channel (their owners were told and the subscription stays); **`failed` when the host cannot export at all** (no exporter, no browser, a font missing): the failure carries the module's words, which name the setting (`views.export`), and the subscription shows the same as its problem.
- **The layout.** A subscription names a layout (`layout: print`) or none; none is the module's own rule for the print medium (plan 32 section 3.6). A document with a failed widget is sent with the error block in its place and the message says which widget and why.

## Hosting

```csharp
var export = new ViewsExportTasks(subscriptionService, stateDatabase);               // opens: delivery, runs
publish(ViewsExportTasks.Manifest(new ViewsExportSettings(Every: TimeSpan.FromMinutes(1))));   // package/1 on the host's system channel
var runner = new ViewsExportTaskRunner(export, executor);                           // the task in process, every other task to the executor
```

The task runs **in the process that has the views module and the install's state** (as `quality-system` does in the host's), because drawing a document as a person needs the serving core. That is the Gateway, or the console of a single-server install. A runner on another machine calling the views module over HTTP with a token made for the subscriber is not built; it is the open decision of plan 22a (GP17). The Gateway wires it (`GatewaySubscriptions`: `SubscriptionRenderer`, `SubscriptionService`, `DeliveryService` and this class over its state, run every minute); it claims nothing until the host registers `IDeliveryChannels`, which a Gateway does not yet (the channel names are the console's bindings). The console does not draw documents and does not run it.
