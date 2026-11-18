# SYNCAUTHORING-1 (work in progress)

## Built

- The frame (`Shared/web/workspace/src/pages/WorkspacePage.tsx`): `/:project/:id/files/<path>` and `/:project/:id/designer/...`, landing by kind,
  `?tab=` addresses opening a tab of Files, the navigation (Designer entries, Files tree), the chrome (not saved, Save, check count, Commit…).
- The draft's unsaved edits in the frame (`draft.tsx`: `DraftProvider`, `useDraft`, `useDraftFile`, `useDraftFiles`).
- The registration (`designers.ts`: `registerDesigner`, `designerOf`, `designerPath`, `filesPath`).
- Sync's placeholder Designer (`designer/syncDesigner.tsx`): the replications as entries, a page naming the replication's files.
- vitest `frame.test.tsx`; Playwright `sync-designer.spec.ts` (first story); `workspace-authoring.spec.ts` clicks into Files for the sync draft.

## Defaults taken

- D1: a form's live check in Files saves (flushes) only text typed in that editor since it opened. Example: Ana turns Enabled off in the Designer,
  clicks the task file in Files: the form shows `enabled: false` marked not saved, and it stays not saved until she presses Save (before, opening
  the form would have saved it within a second).
- D2: the Designer's own unsaved edits are never saved by moving between areas; only Save (chrome or editor) writes. Forms that make files in the
  Files area (New connection, New replication of the set overview, New model) still write straight into the draft as before.
- D3: Delete and Move of a file stay immediate writes to the draft (as before); a file made only as an unsaved edit is deleted by dropping the edit.
- D4: the Designer's entries and the Files tree are both always in the navigation; the kind's overview cards (Sync's set panel, the integration's)
  stay in the Files area only.

## Open

- The Designer→Files direction of "an unsaved edit survives" is in the vitest; the Playwright story checks Files→Designer→Files, because the
  placeholder page has nothing to edit. SYNCAUTHORING-15's story adds the Designer→Files direction (Enabled off, then the task file in Files).
