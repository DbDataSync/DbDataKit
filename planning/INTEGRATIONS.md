# INTEGRATIONS: the integration product

## Goal

Integrations (code that moves data between the suite and systems that are not its databases, authored in the Workspace and run on the Runner) have one
subject for their features: the kit, its sources and targets, triggers and screens. Connections are CONNECTIONS'; how author code is contained is
SANDBOX's.

## What exists today

- The kit (`DbDataKit.Runner.Kit` and its parts), the `integration` project kind, templates, the file and HTTP connections, landing, change
  detection, outbound patterns, recorded HTTP and a fake file share (`docs/integrations.md`).

## Open designs

### A semantic query as an outbound source of the integration kit

Wanted, not urgent (owner, 2026-11-18). The kit's outbound push (`DbDataKit.Runner.Kit.Outbound`) sends the records of a table, a view, a query of a
connection or a Store entity to an API or a file, batch by batch with idempotency keys, resuming after a failure without repeating what was sent. A
semantic query as a source would let an integration send governed numbers (a measure by region) rather than re-deriving them from tables. **Open
questions:** it goes through the Gateway's Flight SQL, which no kit library calls today, so as whom it runs (the task's service account through a
Runner-to-Gateway link, TRUSTZONES) and how its rows get stable keys for the ledger.

## Shared areas touched

- `DbDataKit.Runner.Kit*`, `docs/integrations.md`.
