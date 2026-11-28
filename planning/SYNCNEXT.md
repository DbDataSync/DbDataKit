# SYNCNEXT: Sync's next pass after the Designer

## Goal

What SYNCAUTHORING left for later, kept together until each part is designed and becomes tasks: watching and operating a running replication, the
screens outside a replication (the Replications list, the Scripts list and a script's page), the statements a pass would run, the preflight's
privilege reads on MySQL and Oracle, and the browser story of a Sync project on an installed suite.

The connection pages, the drivers and a connection that serves several databases are SANDBOX's (its hosted connections and connection kinds). A
script's Test moves into the Workspace's sandbox with SANDBOX step 2.

## What exists today

- The Sync Designer covers a replication's authoring (`DbDataWorkspace/README.md`); connections and scripts are edited in Files.
- A replication's runs are watched and operated on the Runner's screens and through the console's run calls (`docs/publishing.md`, "Runs of a deployed
  build").
- The set's preflight reads the target login's privileges on SQL Server and PostgreSQL.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| SYNCNEXT-1 | A Playwright story on the installed suite (CI-5's fixture): a new Sync project, two connections, a login set up from the Designer, the catalog refreshed, a table mapped, built, deployed to `dev` through the console and run; the rows reach the target. A step that fails becomes a task of the subject that owns it (not urgent) | CI-5 | M | the story passes in CI against SQL Server and PostgreSQL from `testenv` |

## Open designs

### The preflight reads privileges on MySQL and Oracle

The set's preflight reads the target login's privileges on SQL Server and PostgreSQL only. When a set writes to MySQL or Oracle in production, it reads them there too (`information_schema.table_privileges` and `SHOW GRANTS`; `session_privs` and `all_tab_privs`), each with a conformance test on its engine.


### Monitoring and operations, second pass

What the old screens did to watch and operate a running replication, removed from this pass, and what they would read now that the Runner runs the set
and the console relays to it. The second pass designs where they live (the Designer reading the Runner through the console's relay as the signed-in
person, or the console's Runner screens with a link from the Designer) before it becomes tasks.

- **Run controls in the chrome**: Run Now, Pause and Resume (with a note asked in a dialog), Bulk Load…, Reconcile deletes…. Now: the Runner's task
  actions (`POST .../tasks/{...}/run|pause|resume`), the console's run calls as the person (`POST /api/deploys/{id}/runs`,
  `docs/publishing.md`, "Runs of a deployed build"); reconcile and resync paused by the owner. Open: how **Enabled** in the draft and a
  pause on the Runner are told apart on one screen (the draft's setting takes effect when published, a pause at once).
- **The rail's Replication status and Last 24h**: running or idle in words, next due, the last run; passes, failures, rows read and written,
  p50/p95/max, a bar per hour, 1h/24h/7d, a refresh countdown. Now: the Runner's task and its runs (`GET /api/v1/tasks/{package}/{channel}/{task}`,
  `GET /api/v1/runs` and their pass records).
- **Current Status**: per mapping the read position, its time and age, intent and hold, lag (exact or estimated), **Manage…** to rewind. Now: the drain's
  checkpoint `watermark` on the Runner (one item per mapping and source table), `GET .../checkpoints`, its history and rewind. Open: what lag means with
  no Sync server measuring it: the age of the position (no read of the source, but a quiet table looks behind), a read-only Runner task that measures it
  on demand, or the drain writing each mapping's lag into its pass record (exact for change tracking and logical slots, estimated otherwise, one query per
  mapping per pass, as the old server paid).
- **Run History, Pause History, Bulk Load History**: filters, paging, a run's dialog with its timing and log. Now: the Runner's runs, steps, logs and
  units, its decisions.
- **Verification results** of a mapping and **quality check results** of a replication: now the Runner's run steps and the console's Quality page.
- **The Runner tab and page**: the version served and staged per channel, slots and bindings, instances. Now: the Builds tab and `PublishForm.tsx`, and
  the Runner's own screens.
- **Tracking state**: the target's units, catch-up and divergences (the target's tracking state is TASKENGINE's).
- **Live updates**: the old screens followed runs over SignalR; the Runner's screens poll.


### Outside a replication: the replications and the scripts

The old Sync screens outside a replication's page, left out of this pass by the owner. Meanwhile a replication is reached from the Designer's navigation
(the draft's replications and **New replication**), and connections and scripts are edited in Files (the generated forms of `config/connections/` and
`config/scripts/`, and Files' Scripts tab, which lists the scripts and tests one against generated input). Where these screens live (Designer pages of
a sync project with their own navigation entries, or Designer pages shared by every kind that has connections) is designed before they become tasks.

- **The Replications list** (`pages/ReplicationsPage.tsx`): a table of every replication with its reader, its schedule as a sentence and its mapping
  count, a row opening the replication, and the form that makes a new one. Needs: nothing beyond the draft model (the files of
  `config/replications/`) and the check's findings per replication; the form itself is restored in this pass as the navigation's **New replication**.
- **The Scripts list** (`ScriptsPage.tsx`): kind, language, where each script is bound, enabled, **New script**. Needs: the draft's `config/scripts/`
  (the `scripts` route exists) and where each script is bound (`Services/ScriptUsageScanner.cs`).
- **A script's page** (`ScriptEditPage.tsx`, `script-edit/ScriptTestPanel.tsx`): the manifest's fields, the code in the code editor, compile
  diagnostics at their lines, the test against generated input, where it is bound. Needs: a script's compile (`ScriptsController` compile, which runs
  the person's code and so stays a child process), where it is bound, and the `script-test` route the Workspace has.


### Preview SQL of a mapping

The old **Preview SQL** tab listed every statement a pass of a mapping would run, in order (the reader's, the staging's and the writer's built-in
statements, the operator's SQL, the scripts' output, the hooks), built by the drivers from the mapping, the tables' metadata and the position the next
pass reads from. The likely authoring shape is a command run by the Workspace on a copy of the draft, as the provisioning plan is, with the catalog cache
as the metadata and "from the first position" as the position. Open: whether every driver builds its statements without a connection (the change
tracking reader asks the server for its version functions), and whether a statement that needs the live server is shown as "built on the Runner" or left
out.


## Shared areas touched

- `Shared/web/sync-ui`, `Shared/web/workspace`, the console's Runner screens, `DbDataSync.TaskRunner` (the preflight), the Playwright stories.
