# SYNCAUTHORING-16 (work in progress)

Branch `backup/syncauthoring-16`, on `backup/syncauthoring-15`.

## Built before this task (SYNCAUTHORING-14, on main)

Checked against the row: the Table Mappings sidebar with column counts and **+**, the overview of the source's tables from the catalog cache with
mapping counts, ticks and **Create mappings** (one file per ticked table through the mapping writer, no `targetType`, no key stated), the mapping
editor (name, Cancel, Delete, Save mapping) with its two sides, **Override for this table** and the table picker from the cache, Column Mapping
(Auto-map by name, source type, target type, transform, the primary key, relationships, the cached-metadata card). vitest flows in
`Shared/web/sync-ui/src/designer.test.tsx`.

## This task

- [ ] the browser story in `sync-designer.spec.ts`: two tables ticked make two mapping files; on one, Auto-map by name maps three columns, a key
      column unticked, the target side's schema overridden; Files shows the same changes
- [ ] coverage rows name the story
- [ ] the row leaves `planning/SYNCAUTHORING.md`

## Open questions

None yet.

## Defaults taken

None yet.
