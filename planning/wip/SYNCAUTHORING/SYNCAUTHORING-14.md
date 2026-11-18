# SYNCAUTHORING-14 (work in progress: not started)

Stacked on `backup/syncauthoring-1` (ee911645a). Nothing of the package is built yet; this file is the handover.

## Built so far

Nothing for this task. What it builds on (ready on the branches below it):

- `@dbdatakit/workspace` exports the frame: `registerDesigner`, `designerPath`, `filesPath`, `useDraft`, `useDraftFile`, `useDraftFiles`,
  `findingsKey`, `useSyncEngines` (`Shared/web/workspace/src/index.ts`).
- Sync's placeholder Designer is `Shared/web/workspace/src/designer/syncDesigner.tsx`, registered in `WorkspaceModule.tsx`. This task moves it into
  `Shared/web/sync-ui` and the console registers it (`registerDesigner(syncDesigner)` when it loads the workspace module).

## Plan for the next worker

- The old screens: `git archive 96601f27^ Shared/web/sync` (the slice's components are about 3,000 lines, written against the old `api/hooks.ts`).
- The draft model `useSyncDraft`: typed views of `task.yaml`, the mappings and the connections (read only); a component's new object is diffed against
  the normalized old one and each changed leaf becomes a path edit (`setAt` / `removeAt` of `@dbdatakit/shell`), so comments and unknown keys survive;
  a file that does not parse, or whose `schemaVersion` is newer (task: 2, mapping: 1), is reported for Files.
- The Designer page can use nested `<Routes>` under `designer/replications/:name/...` (it is rendered under the frame's `/:project/:id/*` route),
  keeping the old routes `overview`, `mappings/overview`, `mappings/:mapping/columns`.
- The mapping editor drives the draft directly (each change is an unsaved edit); **Save mapping** is `draft.saveOne(path)`, **Cancel** is
  `draft.discard(path)`.

## The catalog cache to read (from the AUTHORING worker, `backup/authoring-20` a5b96e44)

- Read the cache with `readCatalogCache` (`Shared/web/workspace/src/catalogCache.ts`, `catalog-cache/2`), from the draft's `catalog-cache.yaml`
  through `useDraftFile` so unsaved edits count.
- Database pickers from `connection.databases`; schema pickers from `schemasOf(connection, db)`; the "where it was read" line from
  `readLine(connection.read)`.
- Tables carry `database`, `uniqueKeys` and `foreignKeys`; `tableLabel` now includes the database.

## Defaults taken

None yet.

## Open questions

None yet.
