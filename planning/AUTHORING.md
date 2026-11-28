# AUTHORING: Workspace authoring of build, sync and store projects

## Goal

Everything a person did on the deleted Build, Sync and Store servers is done from a Workspace project, started where the person works and run where it
belongs: reads of a database as Runner tasks, results back on the same screen. Every authoring screen has a browser story.

## What exists today

- Build, sync, store and integration projects in the Workspace (`DbDataKit.Authoring.Server`), built with the products' tools; the console deploys
  their builds to a Runner and a Gateway as the person (`ConsoleDeploys`, `DbDataKit.Deploy`; `docs/publishing.md`).
- Build's `tests`, `status` and `compare` are manual tasks of the refresh package (`ViewJobs.cs`) on the Runs tab. The operations package
  (`project publish --operations`: `init`, `seed`, `apply_plan`, `ack`) is not built by the Workspace, so those run from the Runner's screens only.
- Sync publish: the platform comes from `WorkspaceSettings.SyncPlatform`, not from the target Runner.
- Sync forms: all YAML-only parts have forms. A mapping's **Preview rows** and a segmenting strategy's **Try** run the set's tasks `preview` and
  `try-strategy` on the Runner through the console, with the mapping as the draft has it (unsaved edits included); their answer is kept nowhere (`docs/authoring-metadata.md`, "Rows and trials are answered, never
  kept").
- `dbdatasync provisioning plan --from-cache` plans the target side offline; the source side (change tracking, logical slots) is `unknown` (the Sync set's `preflight` checks it live).
- A model's `queries:` are edited as YAML only (the Build server's editor went with the server).
- The offline verb `dbdatabuild project publish --to gateway --out` and the per-publish adopt confirmation of a hand-made data source still exist beside
  the slot bindings that replace them.
- The console's build-helper agent installs itself on a Build server project (`ConsoleAgents.InstallBuildHelperAsync`), which no longer exists.
- Many Workspace screens are tested by vitest only, never opened in a browser (`docs/ui-coverage.md`); many browser stories of the console's and
  the Gateway's screens are written and have never run in CI.
- Resync and reconcile-deletes buttons of a sync project's Runs tab are paused (owner, 2026-11-16).
- A package's read-only manual task `preflight` runs before the console serves a version in `prod` and from **Re-check** on the Runs tab
  (`docs/publishing.md`, "Preflight before activation"). Build's refresh package, a Store model's package and a Sync set declare it.
- The Designer's shared reads (`@dbdatakit/workspace`): the channel picker offers **Set up for development** where a channel does not bind the slot,
  `prod` as any other with the production warning (the console stores the login on the Runner, copies the draft's connection there and binds the slot), **Test** on the tab **Catalog**, **Describe columns** for a kind to
  place, and the newest `preflight` per channel on the Runs tab (`docs/authoring-metadata.md`, "Every kind uses the same pieces").
- A draft is read before it is ever published: the picker lists every channel the Runner serves (`GET /api/v1/metadata/channels`), and in a channel
  that holds no version of the project's package `discover`, `describe` and the binding test read the slot the channel binds for it
  (`docs/authoring-metadata.md`, "Before anything is published: Set up for development").

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| AUTHORING-1 | The Workspace builds the operations package beside the refresh package, and the control service deploys both to the same channel (`POST /api/deploys`); the Runs tab offers `init`, `seed`, `apply_plan` and `ack` with their inputs | none | M | `WorkspaceBuildPublishTests`: a build stages both packages; vitest of `ProjectRunsPanel`: the four tasks are offered; a conformance run of `seed` started from the console through the control service |
| AUTHORING-5 | A form editor for a model's `queries:` in a build project (name, description, `sql` or `request`, parameters), checked as you type | none | M | vitest on the form and its YAML; Playwright: add a query with a list parameter, see a translation error at its line, fix it, publish |
| AUTHORING-17 | Adds to the Runner's built-in package `dbdatakit-metadata` (its tasks `discover` and `describe` exist: `DbDataKit.Runner.Metadata`, the route `POST /api/v1/metadata/{channel}/{task}`, `docs/authoring-metadata.md`). Its task `facts`: the server's version and the database settings a kind's checks name, on `DbDataKit.Metadata`'s readers, started, resolved, refused and recorded as `discover` and `describe` are; never a value of a table | none | S | Runner tests on SQL Server and PostgreSQL: `facts` in `dev` answers the server's version and, on SQL Server, whether change tracking is on for the database; an author's `facts` in `prod` runs and its run record names the author |
| AUTHORING-6 | The Designer's shared pieces of authoring-time reads are built (`docs/authoring-metadata.md`, "Every kind uses the same pieces"): the channel picker with **Set up for development**, **Refresh**, **Test**, **Describe columns** (`QueryDescribe`, the console's `POST .../describe`) and the newest-check card (`NewestChecks`, the console's `GET /api/deploys/{id}/runs?task=`). What is left: the console's posture page lists the non-production bindings made from the Workspace (Set up for development makes them: a slot bound to a copy `<package>-<slot>-<channel>` whose password names a `dev-login` secret, `docs/authoring-metadata.md`, "How a screen sends a development login"); **Describe columns** writes a query's columns into a Build model's file (a Sync mapping's are written by the card **Query columns** of `@dbdatakit/sync-ui`) | none | S | vitest of the posture list and of Build's use of the columns; Playwright: after Set up for development binds the slot, the posture page lists the binding |
| AUTHORING-10 | The secrets overview of a Workspace project's connections (decided 2026-11-12, not built): every secret reference of the project, where it is bound, whether the target holds it (read by the control service from the Runner, never by the Workspace service); the Sync parameter form picks a `Secret` parameter's reference from it (today the person types `secret:<name>`) | none | S | vitest and a Playwright story: a project with three references shows each with its binding and state |
| AUTHORING-13 | The form editor of the `access:` section (rules tables, row rules, column rules) in the Workspace's build and store model panels | none | M | vitest: the form round-trips a model's `access:` section; Playwright: add a column rule, see the check's finding for a missing rules table, publish |
| AUTHORING-25 | On SQL Server, `discover` also lists every database the connection's login can open (`sys.databases`), each with its schemas, and reads the tables of the one asked for (an input `database`); the database pickers offer them. A PostgreSQL connection reads its one database, as today | none | M | a SQL Server conformance test: a login that reads two databases sees both in the cache, and the tables of the second after a read that names it |
| AUTHORING-26 | A read of a database's shape (`discover`, `describe`) needs the Runner's viewer role, not operator: it changes nothing, and the console has already checked that the person has access to the project | none | S | a person who is a viewer on the project's Runner refreshes the catalog; starting any other task still needs operator |
| AUTHORING-27 | The person who staged a version held for its preflight activates it: with no error found, the Runner lets that person activate it with the role that staged it (publisher); after an error, the same person confirms the errors, recorded with their name as today. `docs/publishing.md` ("Preflight before activation" and the route table) follows | none | S | a test: Bob, a publisher on the Runner, serves a package in a production channel; its preflight finds nothing and Bob activates it; a second version's preflight finds an error and Bob's activation with `"confirm": true` is recorded with his name |
| AUTHORING-29 | In a group of Runners, a run whose answer the console waits for (**Preview rows**, **Try**) is pinned to the instance that takes the call: that instance queues it for itself only, and the console's call that waits for the answer reaches the same instance, so the rows never leave that instance's memory and the first ask is answered. `docs/authoring-metadata.md` ("What the Runner does with an answer") follows; until it is built, a call that reaches another instance answers `gone` | none | M | a test with a group of two Runner instances behind one address: a `preview` taken by one instance runs there, and the console's answer call returns its rows on the first ask, never `gone` |
| AUTHORING-32 | One shared step that makes registering a Workspace project kind a recipe (schema, check, build, publish, editor), before the agent, action and knowledge editors | none | M | a test kind registered by the recipe alone gets the editor, check, build and publish |

## Needs from other subjects

- Agents authoring Workspace projects (through MCP or the console's embedded agent, each change approved) and the build-helper on a Workspace build
  project are AGENTTOOLS-8.
- The control service's deploy is built (`POST /api/deploys`, `ConsoleDeploys`; `docs/publishing.md`, "Deploying a build from the control service"): AUTHORING-1
  and SYNC-2 (the target Runner's platform) build on it.
- The Gateway catalog refresh is the console's `POST api/workspace-catalog/{workspace}/{project}/gateway-read` (`ConsoleCatalogRead.GatewayAsync`; `docs/authoring-metadata.md`, "A read from a Gateway").

## Open designs

### The Runner the channel picker offers first

The picker offers the Runner the catalog cache says the connection was last read from, or else the first Runner the console lists. Once a project names the Runner it deploys to, the picker offers that one.

### The Runner's built-in package where a group requires signed versions

**What.** The Runner puts its built-in package `dbdatakit-metadata` in a channel through its own publish, so a group with `require_signatures` on
refuses it and a catalog read answers 409 naming the missing signature. The direction is to exempt that package, because it ships beside the Runner's
own program and is as trusted as the program, with the audit line of its install saying so. **Why it waits:** signatures are not built yet, so with
the setting on every publish is refused anyway, and the exemption is a supply chain exception to weigh again when they are. No code changes until then.

## Shared areas touched

- `DbDataKit.Authoring`, `DbDataKit.Authoring.Server`, `Shared/web/workspace`, `Shared/web/console` (posture, secrets overview), `DbDataKit.Agents`.
- `docs/formats.json` and `docs/ui-editors.md` (`gateway-publish/1` removed, `catalog-cache/2`), `docs/ui-coverage.md`, `docs/authoring-metadata.md`.
- The Runner (`DbDataKit.Runner`: the built-in package `dbdatakit-metadata`, the binding test by slot, the held staged version), `DbDataKit.Runner.Client`.
- The console's host (`DbDataKit.Server`: the catalog read from a Runner channel).
