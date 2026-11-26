# WORKSPACEISOLATION-4 (the part that is left): work in progress

Stacked on `backup/workspaceisolation-3`.

## Built so far

- **Preview with data** on the Builds tab calls the console's `POST /api/deploys` with `previewInstance`; the Gateways offered are those the console
  manages; the record of the preview is the console's deploy record.
- vitest `BuildsPanel.test.tsx`, `PreviewDataForm.test.tsx`; the story `workspace-publish.spec.ts` sends the preview through the console.
- Docs: `docs/apps.md`, `docs/ui-editors.md`.

## Not yet run

- vitest, typecheck, the Playwright story.

## Open questions

## Defaults taken
