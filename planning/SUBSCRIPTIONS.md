# SUBSCRIPTIONS: scheduled deliveries and report-catalog reads on Work

## Goal

Subscriptions to pages and custom views, and reads of external report catalogs (SSRS, Power BI Report Server), run as Runner tasks. A subscription is
rendered as the subscriber through path 2 and sent by the delivery service; a subscription stops, telling the person and the owner, when the person loses
access. A catalog's snapshot is published to the linked Gateway group, which serves the merged catalog per person.

## What exists today

- Subscriptions are rows of the console's `delivery` module; the Gateway's `GatewaySubscriptions` (a `BackgroundService`) renders them where the views
  module runs, and the console's sender sends (`ConsoleDelivery.cs`); never verified against a real console and Gateway sharing one SQL Server.
- `DbDataKit.ReportCatalogs`: the SSRS and PBIRS provider, the `report-catalog` connection kind, the snapshot, the merge, the knowledge items, the report
  browser; no host wires them: the catalog read task was to run on the console's embedded runner, which was deleted.
- No binding kind for catalogs in `bindings.yml`.
- A subscription whose person lost a rule's access is not stopped by a rule check (only by what the renderer or the identity home says).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SUBSCRIPTIONS-1 | Subscriptions render on Work: a first-party package with the `views-export` task renders each due subscription as the subscriber through path 2 and hands the PDF to the delivery outbox; `GatewaySubscriptions` goes | TRUSTZONES-10 | M | with the fake channel and clock: two subscribers of one page with different rules receive different rows; the Gateway runs no subscription loop; a run against a real console, Gateway and Runner on one SQL Server (servers suite) |
| SUBSCRIPTIONS-2 | Access loss stops a subscription: a person who loses a rule's access gets one message, the owner one, and nothing more is sent | SUBSCRIPTIONS-1 | S | on three engines: a subscriber removed from the rules' group gets one "stopped" message and no attachment |
| SUBSCRIPTIONS-3 | Scheduled and emailed custom pages: PDF attachment, optional inline PNG, one link to the live page; a failed render tells the owner | SUBSCRIPTIONS-1, PAGEFORMAT-7 | M | a subscription to a custom page delivers the PDF as the subscriber; a render that fails tells the owner |
| SUBSCRIPTIONS-4 | A binding kind for catalogs: `catalogs:` in `bindings.yml` (slot = the catalog's name, one `report-catalog` connection per channel, read-slot semantics), shown in the Bindings panel | none | M | reader and checker tests with file and line; the screen shows the slot; a connection of another kind is refused |
| SUBSCRIPTIONS-5 | The catalog read task on any Runner: the package `report-catalogs` with its task `read`, slots resolved by `RunnerSlotResolver` for the kind `report-catalog`, the snapshot published over TRUSTZONES-9 | SUBSCRIPTIONS-4, TRUSTZONES-9 | M | a Runner task reads a recorded SSRS catalog and the linked Gateway serves its snapshot; "read now" is the Runner's run-now |
| SUBSCRIPTIONS-6 | The Gateway's half of the catalog host: a provider per catalog from what its channel serves, the merged view per person, the console's relay of `/api/v1/catalog...`; the app manifest's `catalogs` and the instance's `frame` origins reach the merge | SUBSCRIPTIONS-5 | M | `DbDataGateway.Tests`: an item the person may see is listed, one they may not is not; the console's Catalog tab lists both providers |
| SUBSCRIPTIONS-7 | A real report server: record the SSRS and PBIRS answers the tests replay, and the Windows sign-in of the `report-catalog` connection | SUBSCRIPTIONS-5 | S | checked by hand on a Windows host with a report server (none in CI); the recordings replace the hand-written fixtures |

## Needs from other subjects

- TRUSTZONES-10: governed reads through the linked Gateway as a named person.
- TRUSTZONES-9: publishing from a Runner task to the linked Gateway group.
- PAGEFORMAT-7: custom pages as pages, before SUBSCRIPTIONS-3.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.Delivery`, `DbDataKit.Views.Export`, `DbDataKit.ReportCatalogs`, `DbDataKit.Runner.Kit` (slot resolver), the bindings reader (`bindings: 1`).
- `docs/formats.json` (`bindings` additive), `docs/ui-editors.md`, `docs/views.md`.
