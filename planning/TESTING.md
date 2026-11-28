# TESTING: the tests that prove each screen and service works

## Goal

Every screen has a browser story that runs in CI, and the stories that exist but never run do run. Tests that need the suite installed start from one
fixture. Which tests exist and what they cover is this subject's; when and where they run (the tiers, the weekly run) is CI's.

## What exists today

- Playwright stories under `DbDataKit/tests/DbDataKit.Server.Web.Tests/tests`; some are written and not run in CI (`docs/ui-coverage.md`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| TESTING-1 | Browser stories for the Workspace screens never opened in a browser: column lineage, model files from cached tables, the provisioning plan, scripts, the slot bindings screen, the Sync forms, the catalog refresh form; and the stories written and never run in CI: `workspace-authoring.spec.ts` (the kind picker on a new project with what each kind publishes to, check, fix, and the publish panel by product for a build, a sync and a store project), `workspace-publish.spec.ts` (create, edit, build, preview, commit, merge, publish an app) and the preview with live data; each listed in `docs/ui-coverage.md` | none | M | the Playwright specs run in CI; `scripts/check_ui_coverage.py` passes with the stories named |
| TESTING-2 | Browser stories of the console's own screens that are written and never run in CI, run there like TESTING-1: the agents' evaluation panel (`screens-agents-evaluation.spec.ts`), the Runner's Link flow, the store approvals (`store-approvals.spec.ts`), the monitoring story against real servers (`monitoring.spec.ts`), and the others `docs/ui-coverage.md` lists as not run | none | M | the Playwright specs run in a CI job of the console; `docs/ui-coverage.md` no longer lists them as not run |
| TESTING-3 | Browser stories of the Gateway's screens (shown by the console) that are written and never run in CI, and the missing ones: the semantic model browser and query explorer (`semantic-models-explorer.spec.ts`), the result cache screen (a dataset's hit rate rises after repeated queries, Clear empties it and the next query is a miss), click-to-sort of tables and matrices (`views-sort-print.spec.ts`), the stories that need a real Gateway (`views-apps-addresses`, `views-apps-embed`), and the `semantic-models.spec.ts` and `refresh-run.spec.ts` stories that need the test PostgreSQL | none | M | the Playwright specs run in a CI job against a real Gateway; the cache story passes; `docs/ui-coverage.md` no longer lists them as not run |
| TESTING-4 | An installed suite as a test fixture (not urgent; manual tests from a snapshot release meanwhile): a CI job on Linux installs the packaged suite with `install.sh`, runs `dbdatakit setup single-server`, enrols the first administrator from the setup link in `FIRST-RUN.txt` with a virtual passkey authenticator (as `tests/people.ts` does) and saves the signed-in browser state, the console's address and the testenv engines' addresses; any later story starts from that state instead of starting services from their build folders. Linux first; Windows (the MSI) later | none | M | the job installs, joins all four services, signs in, and a trivial story reusing the saved state opens the Servers page with four services joined |

## Shared areas touched

- the Playwright stories and their config, `docs/ui-coverage.md`, `testenv/`.
