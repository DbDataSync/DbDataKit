# LINEAGE: lineage across the suite

## Goal

Lineage covers what the suite does not see on its own: entries a person declares for outside systems, an export in the OpenLineage format, and tracing
a value from a report back to where it came from. Lineage the services record themselves stays with each service.

## What exists today

- `DbDataKit.Lineage`: lineage recorded by the Runner's tasks, the Gateway and Build; the console's lineage graph.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| LINEAGE-1 | Manual lineage entries (a file `lineage-entries/1`, then a console form), OpenLineage export, tracing a value from a result back to its sources (queued, low priority) | none | L | each: the entry is drawn apart in the graph; an export validates against OpenLineage's schema; a dashboard cell opens its path |

## Shared areas touched

- `DbDataKit.Lineage`, the console's lineage screens, `docs/formats.json` (`lineage-entries/1`), `docs/ui-editors.md`.
