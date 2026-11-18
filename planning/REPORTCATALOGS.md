# REPORTCATALOGS: outside report servers' catalogs inside the suite

## Goal

People find the reports that live on outside report servers (SQL Server Reporting Services, Power BI Report Server) inside the suite. A Runner task reads
each catalog as its service account and publishes a snapshot to the linked Gateway group, which shows it filtered to what each person may see; each entry
links out to the report on its server.

## What exists today

- `DbDataKit.ReportCatalogs`: the SSRS and PBIRS provider, the `report-catalog` connection kind, the snapshot, the merge, the knowledge items, the report
  browser; no host wires them: the catalog read task was to run on the console's embedded runner, which was deleted.
- No binding kind for catalogs in `bindings.yml`.
- The SSRS and PBIRS answers the tests replay are written by hand from the servers' documentation; no real report server has been recorded.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| REPORTCATALOGS-1 | A binding kind for catalogs: `catalogs:` in `bindings.yml` (slot = the catalog's name, one `report-catalog` connection per channel, read-slot semantics), shown in the Bindings panel | none | M | reader and checker tests with file and line; the screen shows the slot; a connection of another kind is refused |
| REPORTCATALOGS-2 | The catalog read task on any Runner: the package `report-catalogs` with its task `read`, slots resolved by `RunnerSlotResolver` for the kind `report-catalog`, the snapshot published over TRUSTZONES-9. The snapshot carries the report server's address (not a secret: the person's own browser signs in to the report server), so the Gateway builds each entry's link without a connection of its own | REPORTCATALOGS-1, TRUSTZONES-9 | M | a Runner task reads a recorded SSRS catalog and the linked Gateway serves its snapshot; each entry's link is built from the address in the snapshot with no connection on the Gateway; "read now" is the Runner's run-now |
| REPORTCATALOGS-3 | The Gateway's half of the catalog host: a provider per catalog from what its channel serves, the merged view per person, the console's relay of `/api/v1/catalog...`; the app manifest's `catalogs` and the instance's `frame` origins reach the merge | REPORTCATALOGS-2 | M | `DbDataGateway.Tests`: an item the person may see is listed, one they may not is not; the console's Catalog tab lists both providers |
| REPORTCATALOGS-4 | A real report server: record the SSRS and PBIRS answers the tests replay, and the Windows sign-in of the `report-catalog` connection | REPORTCATALOGS-2 | S | checked by hand on a Windows host with a report server (none in CI); the recordings replace the hand-written fixtures |

## Needs from other subjects

- TRUSTZONES-9: publishing from a Runner task to the linked Gateway group.

## Proposed decisions

None.

## Shared areas touched

- `DbDataKit.ReportCatalogs`, `DbDataKit.Runner.Kit` (slot resolver), the bindings reader (`bindings: 1`), the Gateway's catalog routes.
- `docs/formats.json` (`bindings` additive), `docs/ui-editors.md`, `docs/views.md`.
