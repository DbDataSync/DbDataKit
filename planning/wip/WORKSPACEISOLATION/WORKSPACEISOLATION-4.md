# WORKSPACEISOLATION-4: work in progress

## Built so far

Nothing yet: started from `origin/backup/workspaceisolation-3` (47cf403f9).

## Plan

- The Builds tab's publish panel (`PublishForm.tsx`, `BuildsPanel.tsx`) calls the console's `POST /api/deploys`; its targets are the Runners and
  Gateways `GET /api/instances` lists, offered when the kind's route goes to their product; the record is `GET /api/deploys`.
- The panel is addressable: `?tab=builds&build=<id>`.
- vitest of the panel's calls; Playwright story against `workspace-fake.ts` and a fake of the console's deploy.

## Open questions

## Defaults taken
