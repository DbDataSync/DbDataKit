# SYNCAUTHORING-15 (work in progress)

Branch `backup/syncauthoring-15`, from `origin/main` with `origin/backup/authoring-24` merged (the shared `ChannelPicker` and `CatalogRefresh`).

## Built before this task (SYNCAUTHORING-14, on main)

Checked against the row: New replication in the navigation as a dialog (name and schedule, the line and link when the draft has no connection),
the replication's page at `designer/replications/<name>` with the breadcrumbs, the tabs Overview and Table Mappings, **Enabled** (with its title),
**Save settings**, **Delete** asked twice, the endpoint cards with the connection and database pickers from the draft and the catalog cache, the
"inherited by N mappings unless overridden" note, the schedule strip, and the line on a card whose connection has no cache. vitest flows in
`Shared/web/sync-ui/src/designer.test.tsx`.

## This task

- [ ] the browser story in `sync-designer.spec.ts` against `workspace-fake.ts` (a catalog cache in the fake's sync draft)
- [ ] `docs/ui-coverage.md` and `docs/ui-editors.md` rows name the story
- [ ] the row leaves `planning/SYNCAUTHORING.md`, its id leaves the other rows

## Open questions

None yet.

## Defaults taken

None yet.
