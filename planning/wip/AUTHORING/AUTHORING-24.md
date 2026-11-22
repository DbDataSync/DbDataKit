# AUTHORING-24 (work in progress)

Branch `backup/authoring-24`, started from `backup/authoring-21`. Waits for `@dbdatakit/sync-ui` and the draft frame (`useDraft`, PR #465) on main.

## Built so far

- `Shared/web/workspace/src/catalogRefresh.ts`: the console's read routes (channels, read, follow), the difference of a refresh (added, removed,
  retyped), the cache with the new entry in place, the refusal of a cache the strict reader refuses.
- `Shared/web/workspace/src/pages/CatalogRefresh.tsx`: `ChannelPicker`, `RefreshDifference`, `CatalogRefresh` (waits, then the run's link and
  following, the difference, Accept or Dismiss).
- The shell sends `/api/workspace-catalog/...` to the console, whatever module is mounted (`HOST_OWN`).
- vitest of the logic and of the picker and difference (static markup).

## Not built yet

- The Catalog tab's Refresh beside its Gateway refresh, reading the cache through the draft frame (`useDraftFile`) and writing through `useDraft().edit`.
- Exported for the kinds' Designers from `@dbdatakit/workspace`.
- A happy-dom test of the whole flow; the Playwright story against `workspace-fake.ts` and a fake of the console's read.
- Docs.

## Defaults taken

- A read of one table that no longer finds the table removes it from the cache (shown as "1 table removed" first). Example: Ana refreshes
  `dbo.accounts` after the DBA dropped it; the difference says the table is removed, and Accept takes it out of the cache.
- The Runner offered first is the first paired Runner the console lists. Example: a console with Runners `runners-eu` and `runners-us` offers
  `runners-eu`; Ana picks the other in the picker.
- A channel that does not bind the slot is listed but cannot be picked.
