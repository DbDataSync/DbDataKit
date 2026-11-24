# AUTHORING-3 (work in progress)

The mapping preview and the strategy try-run as Runner tasks of the published set, started by the console as the person, the preview's rows returned in
the call's answer and never stored.

## Plan

- Runner protocol: a task hands the Runner an answer (`Answer`), which the Runner keeps in memory only, for the run, until the console takes it once or it
  expires; never in the state, a step, the log or the audit.
- Runner API: `GET /api/v1/runs/{id}/answer?on_behalf_of=` hands it to the console that started the run, for the same person.
- Sync set package: manual read-only tasks `preview` (first rows of a mapping's source, with the run login) and `try-strategy` (a segmenting strategy's
  candidates, its SQL run on the bound login).
- Console: `POST /api/deploys/{id}/runs` with `"answer": true` waits for the answer; `GET /api/deploys/{id}/runs/{run}/answer` follows it.
- Designer: **Try** on the Bulk Load sub-tab and **Preview rows** of a mapping, through `@dbdatakit/workspace`.

## Built so far

Nothing yet.

## Open questions and defaults

(filled in as they come)
