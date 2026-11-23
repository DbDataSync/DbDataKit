# WORKSPACEISOLATION-11 (work in progress)

## Built

- `StoreRowsPanel` lists the Gateways from the console (`GET /api/instances`, product `dbdatagateway`), asks each one's store through the relay which
  models it serves (`GET /relay/<id>/api/v1/store/models`) and offers those that serve the chosen model. No Workspace route is asked.
- vitest: `StoreRowsPanel.test.tsx` (7 tests).

## Not built yet

- The Rows story (Playwright) with `publish-targets` answering 404.
- Docs: `docs/ui-coverage.md` row.

## Defaults

- A Gateway whose store answers 403 or 404 counts as serving no model to the person (as the console's Approvals page treats it); one that answers
  anything else, or that the console cannot reach, is named under the pickers with the reason, and the others are still offered.
