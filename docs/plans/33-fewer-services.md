# Plan 33: Fewer services to install

Status: **proposed** (2026-11-10, the owner's direction). The suite installs as up to eight separate servers today. The owner wants **three or four service types**:
the **Console with the Workspace in it**, the **Gateway** and the **Runner**, with Store, Sync and Build becoming *authoring* that live in the
console and workspace area and publish to the runner. This plan says what exists, what has to be true so that merging does not weaken security or deployment,
and the slices. Nothing here is built. Same rules as the other slice plans: one slice, one commit in a batch, "done" is a test, the status goes in the row.

## 1. What exists today (read in the code, 2026-11-10)

| Server | Own install (zip / MSI / Scoop / image) | What only it does |
| --- | --- | --- |
| Console `dbdatakit` | yes / yes / yes / yes (`DbDataConsole/dist.json`) | sign-in, users, pairing, the Servers screens, an **embedded runner** (`console-runner`) |
| Gateway `dbdatagateway` | **win-x64 only** / yes / yes / yes (`DbDataGateway/dist.json`; the release workflow builds a Windows zip only) | the serving layer: Flight SQL, the apps host, the semantic route, the `store` module that serves master-data reads and writes |
| Runner `dbdatarunner` | win-x64 and linux-x64 / yes / yes / **no image** | runs the tasks of published packages |
| Sync `dbdatasync` | win-x64 and linux-x64 / yes / yes / image built, not pushed | replication API and its screens (the engine is moving to the runner, plan 28c) |
| Build `dbdatabuild` | win-x64 and linux-x64 / yes / yes / yes | the CLI, and a server over a project (`server run`, a `service` command, screens in the console): models, plans, checks. `dist.json` registers no service |
| Store `dbdatastore` | **none** (a Dockerfile and a compose file only) | authoring and approving `model/1` definitions, structure changes, change requests, publishing models to a Gateway |
| Workspace `dbdataworkspace` | **none** (no `dist.json`, no Dockerfile, no release job) | git workspaces for projects, app builds in a child process with no network, preview, publishing to a Gateway |

What is already shared: every server has the same shape (plan 13: `server run`, `pair`, state module, health), the same pairing with the console, and the
shared libraries (`DbDataKit.Authoring` is used by Build and Workspace; `DbDataKit.Store` is used by the Store server and the Gateway's `store` module).
**The precedent for folding a server into another is the console's embedded runner**: an entry with a fixed id, flagged `embedded`, never paired, served at
`/api/embedded-runner/...` and `/relay/console-runner/...`, the same screens as a paired runner.

## 2. Direction and what it means

1. **Three service types to install:** Console (with Workspace), Gateway, Runner. A fourth is allowed only where a security or scale reason is written down here.
2. **Workspace folds into the Console** (section 3), **but the first release for testers does not wait for it** (owner, 2026-11-10): the quickest path to a simplified install is the `dbdatakit-full` package (section 6a) with every server that exists as a member, the Store and the Workspace packaged as they are (a zip each, SC16 and SC17). The folds then happen in either order and in small steps, each one dropping a member from the list.
3. **Gateway gets Linux builds** (section 4).
4. **Store, Sync and Build become authoring**: their authoring moves into the console and workspace area (section 5), and what a person authors there (models, replications, projects) is
   *published* as a package. What runs on a schedule runs on the Runner; what is served is served by the Gateway. Their standalone servers keep working
   as they are until their authoring has moved: merging them is lower priority, and only for deployment and maintenance simplicity.

## 3. Workspace into the Console

**What must not get weaker.** These are the properties the standalone Workspace has today; each gets a test (SC04) before the fold, and the fold keeps them:

| Property | Today (Workspace alone) | After the fold |
| --- | --- | --- |
| App code never runs in the host process | builds run in a child process, `isolation: required\|auto`, no network, from a vendored store | the same child process; **the console process never loads or executes app code**. A crash, a runaway build or a memory limit kills the child, never the console |
| The build has no secrets | Bun runs in a network namespace with a cleared environment (`WorkspaceBuilds`) | the child's environment is built from an allow-list, not inherited from the console, so it never holds the console's data protection key ring, signing key, session secrets or any `DBDATAKIT_*` variable |
| The publishing key is separate | one key per Gateway target, held by reference in the secret store; the host reads it only to publish | stays a reference in the **secret store under a name of its own**; only the publish step reads it; it is never in the build child's environment or reachable from a console screen (people see the reference, never the key) |
| Roles are separate | the roles of the Workspace's own pairing with a console (`server pair --max-role`) | a **`workspace` role namespace** next to the console's: console administrator does not imply workspace author or publisher; the two are granted separately, and the publish act needs its own role |
| Folders are separate | one server directory (`projects/`, workspaces, builds) | one folder `workspace/` under the service's directory (`DbDataKit\\server\\workspace`, section 6b), outside the console's own state and key ring, owned by the same service account, with the build child unable to read above its own workspace |
| State is separate | module `workspace` in its own state database | module `workspace` in the console's state database (modules never share tables; the retention and layout tests already enforce it) |
| Audit | the Workspace audits and names the person | the same events in the console's audit, with the module named |

What does get weaker, and how it is covered: **one process now holds both the sign-in and the git and build work**. A bug in the git or archive handling is then a bug
in the process that signs people in. The mitigations are the ones above (no app code in-process, scrubbed child, separate key reference) plus: git and archive
work stays in the shared library with its path tests (traversal, symlinks, hooks disabled), and the unit file or container for the console runs under a service
account with no rights beyond its own directories. If the review in SC04 finds a property that cannot be kept in one process, the fallback in decision D1 applies.

**How.** The Workspace server becomes a library the console hosts, like the runner: the entry `console-workspace` (flagged `embedded`), its management API at
`/api/embedded-workspace/...` and `/relay/console-workspace/...`, the same Workspaces screen as for a paired Workspace server, the verbs under
`dbdatakit workspace ...` (`project`, `workspace`, `publisher`). The standalone `dbdataworkspace` host is removed once the fold is done; it has never been
released, so there is no format or install to migrate and no clean-break message is owed. Publishing to a Gateway uses the console's existing pairing machinery
(a `publisher` pairing with the Gateway), unchanged. The console's package carries what builds need (the pinned `bun`, the vendored package store location from
`apps.packageStore`); a console with no `packageStore` does not build, as the Workspace today.

## 4. Gateway on Linux

The code is not Windows-only. The Gateway's CI runs Linux and Windows suites and `DbDataGateway/Dockerfile` builds a Linux image that the suite release pushes.
What is missing is the package. What is genuinely Windows-only stays so and says so: Analysis Services (`ssas`) sources, Windows UPN password sign-in, and
the 32-bit ODBC worker (`DbDataGateway.Worker`, x86). On Linux the config that asks for them is refused at start with a message naming the setting
(it already is for `ssas`), and the Linux zip does not carry the worker.

The Runner's release job is the pattern: one Ubuntu job builds `win-x64` and `linux-x64` framework-dependent zips, smoke-tests the Linux zip, and the MSI is built
from the Windows zip. Linux gets the zip, `install.sh` and the systemd unit (`dbdatagateway service unit`); Scoop and MSI stay Windows.

## 5. Store, Sync and Build as authoring

The meaning: *authoring* is the part of a product where a person (or an agent) writes definitions. Its output is a **published package or definition**; the
runtime part is somewhere else.

| Builder | Author in the console and Workspace | Published as | Runs / served by |
| --- | --- | --- | --- |
| Sync | replications, mappings, connections as project files | a `package/1` with a `sync` runtime (plan 28c: E-slices) | the Runner |
| Build | models, tables and views, checks, refreshes | a `package/1` (refresh and quality tasks) and, for serving, `connection/2` and semantic models | tasks on the Runner; serving on the Gateway |
| Store | `model/1` files, change approval | the model, published to the Gateway (plan 20 O10b) | the Gateway's `store` module serves the rows; a scheduled step, if any, on the Runner |

Plan 28c already covers how Sync's and Build's engines run on the Runner; this plan covers only where they are *authored and published from*. Each builder
needs its own slice list (a project kind in the Workspace, plan 27 style) and is **not sliced here**: SC20 to SC22 are placeholders that say what must be
settled first.

## 6. Installing and joining on one server

Today each product installs on its own and is joined with a code (`<program> server pair`, then `dbdatakit instance add`) or by announcement
(`DBDATAKIT_CONSOLE_ANNOUNCE_URL`, confirmed in `instance pending`). The compose files use announcement; the Windows installers do not. With three service
types the single-server install is Console, Gateway and Runner, so SC10 makes that case one step per product, without auto-approving anything: the MSI and
`install.*` accept the console's announce address and set it, the server then waits in the console's pending list, and an administrator confirms. A
server that joins itself unasked would be a weaker pairing, so it stays a human click or one command.

## 6a. One Scoop package for a single server

The owner's addition (2026-11-10): a **suite package**, `dbdatakit-full`, for one machine that runs everything. One `scoop install -g` downloads every member's own zip, registers
each service and pairs them, and one `scoop uninstall -g` unregisters the services. It is a fourth route beside each product's own manifest, not a replacement:
the per-product manifests, MSIs and scripts stay for machines that run one product.

**What it is.** One Scoop manifest, pinned to one suite release (every product of a suite release has the same version string, `dist/README.md`), with the members' zips
as a list of `url`s and `hash`es, each extracted into its own folder (`extract_to`: `console`, `gateway`, `runner`, ...) and each program on the path (`bin` with
aliases, so `dbdatakit`, `dbdatagateway` and `dbdatarunner` all work). It is generated by `dist/dist.py` from a new `dist/suite.json` (the member products, in start order) and the
members' own `dist.json`, so a member's service name and register command are written once. It is published like the other manifests: into fixed `dbdatakit-full/dist`, `dist-beta` and
`dist-snapshot` releases, so `scoop install -g https://github.com/<owner>/DbDataKit/releases/download/dbdatakit-full/dist/dbdatakit-full.json` never changes.

**Members, first release (for testers).** Console, Gateway, Runner, Sync, **Store and Workspace as they are**, each its own zip (the last two need SC16 and SC17 first; neither gets an MSI or a Scoop manifest of its own, which the package does not need). **Build is a member too** (SC18): it is a command line *and* a server (`dbdatabuild server run --project <dir>`, paired with the console, whose Build screens are shown through the relay; its own `ui web` is gone, plan 13 R09) and it already has a `service` command, but its `dist.json` registers no service, so a package that registers services would leave its screens unreachable. **When a server folds into another** (the Workspace into the console, SC05; later the Store's or Sync's authoring), its line leaves `dist/suite.json` in the same commit, and the install is that much smaller; nothing else about the package changes. The list is the file's, so a member is a line, not code.

**First release: packaging only (owner, 2026-11-10).** The first `dbdatakit-full` has the manifest, the zips and the `bin` aliases and **no `post_install` and no `pre_uninstall`**: `scoop install -g` puts every member's program on the machine, and the manifest's notes list, in order, the commands an administrator runs by hand (`<program> service install`, `server pair`, `dbdatakit instance add`). The owner tests that first, then the install script below (SC12 to SC14) follows. SC11, SC16 and SC17 are this release; SC18 (Build's service block) belongs to the install script, since a package that registers no service has no use for it.

**What the install does (`post_install`).** Only for a global install (`$global`, elevated: a service belongs there; a per-user install stops with a message saying to use `-g`),
and refusing at the start, before it changes anything, when a member's service is already registered from another folder (a product installed by its own MSI or Scoop manifest:
the routes share service names, so one route per machine). Then, in the member list's order:

1. `<program> service install` for each member (registered stopped, as the MSI does);
2. start the console, wait for its health check;
3. for each other member: `<program> server pair --max-role <ceiling>` prints a single-use code, `dbdatakit instance add <address> --code <code>` consumes it (the same two commands
   `dist/tests/msi-pairing-e2e.ps1` already runs), then start the member. **Pairing here is not auto-approval**: whoever runs `scoop install -g` is an elevated administrator of the
   machine and is the person who would have typed those commands; a code is made, used once and never written to a file, the log or the notes, and the console pins the
   member's certificate as in any pairing. The ceilings are what plans 28a R19 and 13 W12 give a console administrator (`viewer,operator,admin` for a Runner and a Gateway; the Gateway and
   the Runner also get a `publisher` pairing for the console's Workspace once SC05 is done);
4. print what an administrator does next: the console's address and the first-run invitation file (`FIRST-RUN.txt`). No state settings are written (the rule in `dist/README.md` stands).

It is **idempotent**: a member whose service is registered is not registered again, and a member the console already lists (`instance list`) is not paired again, so `scoop update` and a
repeat after a failure converge. A failure at one member stops with the member named, what was done and the command that finishes it, and leaves the others as they were.

**What the uninstall does (`pre_uninstall`, only when Scoop is really uninstalling, not updating).** In reverse order: `dbdatakit instance remove` for each member, `server unpair` there, stop the
service, `service uninstall`. Settings and data under `%ProgramData%` stay, as with the MSI (a purge is the administrator's, by hand, and the notes say where). An update stops the services,
replaces the files and starts them again without unregistering them or pairing them again; the services run from Scoop's `current` folder so a new version needs no new registration.

**Not the same as SC10.** SC10 makes separately installed products join by announcement, where nobody has typed a code. The suite package pairs with codes because one administrator
runs the whole install on the machine.

## 6b. Names and folders (owner, 2026-11-10)

**The product is DbDataKit.** "DbDataConsole" and "DbDataKitConsole" go: the program is already `dbdatakit`, the release tag prefix, the package and the image are already `dbdatakit`, and the Workspace folds into it (section 3), so the one service that holds the sign-in, the pairing, the screens, the embedded runner and the workspaces is **DbDataKit**. "The console" may stay in prose as a plain noun for the web app; it is not a product, folder, project or service name.

**In the repository:** the top-level `DbDataKit/` folder (the shared libraries) becomes **`Shared/`**, and then `DbDataConsole/` becomes **`DbDataKit/`**, so the folder of the same name as the product is the product. The packages keep their names (`DbDataKit.*`, they are the libraries of the suite); the product's projects and namespaces `DbDataConsole.*` become `DbDataKit.Server*` (a name that does not collide with a library package is checked by the build), and `DbDataKit.slnx` keeps its name. The order is fixed: `DbDataKit/` to `Shared/` first, so the second move never meets an existing `DbDataKit/`. About 166 files mention the path `DbDataKit/` and 144 mention `DbDataConsole` (15 of them workflows), so this is a scripted change like the Proxy to Gateway rename (`scripts/rename_proxy_to_gateway.py`), done alone in a batch with nothing else in flight, because every open branch would conflict with it.

**On the machine:** one folder under one root for every service, named for the service. Windows `%ProgramData%\DbDataKit\<service>`, Linux `/var/lib/dbdatakit/<service>`, with the layout `DbDataKit.Hosting.WindowsService.DirectoryLayout` already has inside it (`config`, `data`, `secrets`, `certs`, `logs`):

| Service | Today (Windows) | New |
| --- | --- | --- |
| DbDataKit (the console and the Workspace) | `DbDataKit\console` | `DbDataKit\server` (default; see D9) |
| Gateway | `DbDataGateway` | `DbDataKit\gateway` |
| Runner | `DbDataRunner\server` | `DbDataKit\runner` |
| Sync | `DbDataSync` | `DbDataKit\sync` |
| Build | (a project folder, set by hand) | `DbDataKit\build` |
| Store | `DbDataStore\server` | `DbDataKit\store` |
| Workspace | `DbDataWorkspace\server` | inside DbDataKit's own folder once it is folded (`DbDataKit\server\workspace`); `DbDataKit\workspace` while it is its own service |

One root setting (`DBDATAKIT_DATA`, default the root above) moves them all; each product's own variable (`DBDATARUNNER_SERVER_DIR`, `DBDATAGATEWAY_...`, and so on) still wins for that one service, and `globalEnv` in each `dist.json` is rewritten to the new names. The container images keep their `/data` volumes, which is not a path this plan changes.

**No migration (owner, 2026-11-10).** The suite is in the middle of a large clean break, so nothing here builds or keeps a way to move an existing install: the new services use the new folders and the new service name, the old folders and the old `DbDataKitConsole` registration are not read, moved or renamed, and a tester who has one removes it by hand. The one thing kept is the release note (SC31) that names the old and the new folders and the old service name, so nobody looks for their data in the wrong place. A data root that already holds something from an older layout is not touched.

## 7. Decisions (defaults taken; change any)

| # | Decision | Default taken (recommendation) |
| --- | --- | --- |
| D1 | Fold the Workspace into the console, or package it as its own service | **Both, in this order (owner, 2026-11-10):** package it as it is now as a zip for the `dbdatakit-full` member list (SC17: no MSI, no manifest, no image), so testers get a simplified install first, then fold it (section 3). The packaging is thrown away when it folds, which is cheap (a `dist.json`, a script and a release job). SC04 still decides whether the fold can keep the security properties; if not, the Workspace stays a member |
| D2 | "Publish to the runner" for Store | Store's models still go to the Gateway to be served, because the Runner does not serve; the Runner gets only what runs on a schedule. **Confirmed by the owner, 2026-11-10** |
| D3 | The standalone Store, Sync and Build servers | keep working until their authoring is in the console; then removed with a clean-break message naming where it went |
| D4 | The console's embedded runner and a separate Runner | both stay: the embedded runner stops with the console (production needs the recorded `accept-production`), which is the reason a separate Runner stays a service type |
| D6 | The suite package's name, place and members | `dbdatakit-full`, its own fixed releases (`dbdatakit-full/dist`, `dist-beta`, `dist-snapshot`), members from `dist/suite.json`: Console, Gateway, Runner, Sync, Build, Store and Workspace at first, fewer as they fold |
| D7 | Pairing inside the suite package | codes, by an elevated administrator at install time, never stored (section 6a); announcement stays for separate installs (SC10) |
| D8 | Uninstall | unregisters and unpairs the services, keeps settings and data; an update never unregisters |
| D9 | The folder name of the DbDataKit service itself under `DbDataKit\` | **`server`** (owner, 2026-11-10), next to `gateway`, `runner`, `sync`, `build` and `store` |
| D10 | Product name | DbDataKit (owner, 2026-11-10): the service, its Windows service and its folder; the program, tag, package and image already are |
| D11 | Repository folders | `DbDataKit/` to `Shared/`, then `DbDataConsole/` to `DbDataKit/`, in one scripted batch with nothing else open (owner, 2026-11-10) |
| D12 | Existing installs | **no migration** (owner, 2026-11-10): the clean break stands, with a release note naming the old and new folders and the old service name; no `data move`, no service-name transition |
| D5 | The Gateway on Linux | the zip and `install.sh` and a systemd unit; the Windows-only features refuse on Linux with a message |

## 8. Slices

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| SC01 | **Gateway linux-x64 package**: `dist.json` lists `linux-x64`; the release job builds both zips on Ubuntu like the Runner's (`scripts/package.sh`), the Linux zip without the x86 worker, smoke-tested | none | S | a dry-run of the release carries `dbdatagateway-v<v>-linux-x64.zip` and a verifying `SHA256SUMS`; the Linux zip starts and answers its health check |
| SC02 | **Gateway on Linux, tested as installed**: the install scripts' fake-release test covers the Gateway; the systemd unit and `service doctor`; Windows-only settings refused on Linux with the setting named | SC01 | S | `dist-ci` installs the Linux zip with `install.sh`; a unit test per refused setting; `service unit` output golden |
| SC03 | Docs: `dist/README.md` (the table, the Linux row), the Gateway README (the "Linux path has not been run" line replaced by what was run), release notes | SC01 | S | `check_docs_commands.py` passes |
| SC04 | **Security review of the fold, as tests**: the build child's environment has no `DBDATAKIT_*`, key ring or secret; no network; the publish key is readable only by the publish step; a console role does not grant a workspace role; path tests for git and archive handling | none | M | each row of the section 3 table has a test that fails if the property is lost; the result decides D1 |
| SC05 | **The Workspace hosted in the console**: `console-workspace`, `/api/embedded-workspace/...`, the relay, the screens, module `workspace` in the console's state, audit | SC04 | L | the Workspaces story (create a project, edit, build, preview, publish to a Gateway) passes against a console alone; the standalone host and the embedded one pass the same API tests |
| SC06 | **Roles**: the `workspace` namespace (author, publisher, admin), its screens and the pairing ceilings | SC05 | M | a console administrator without a workspace role is refused every Workspace call; the publish act needs the publisher role |
| SC07 | **Verbs and removal**: `dbdatakit workspace ...`, the standalone `dbdataworkspace` host and its slnf, `CLAUDE.md`, plans 22a and 27 text and `DbDataKit.slnx` updated | SC05 | M | `check_solution.py` and the docs checks pass; no reference to `dbdataworkspace` remains but this plan's history line |
| SC08 | **The console's package builds apps**: `bun` and the vendored store location in the console's zip, MSI and image, with the child's account and folders | SC05 | M | a fresh install of the console package builds the template app with the network blocked |
| SC10 | **Joining on one server**: the MSI and `install.*` take the console's announce address; the product starts waiting in the pending list; the single-server install of Console, Gateway and Runner is documented as three steps | none | M | `dist-msi-ci` installs the console and a second product's MSI with the property and the product shows in `instance pending` |
| SC16 | **Status: built (2026-11-10); the Windows `service install` / `server run` check is left for CI or the owner.** **The Store as a zip**: `DbDataStore/dist.json` (service `DbDataStore`, `service install`/`uninstall`), `scripts/package.sh` like the Runner's, a release workflow like `dbdatarunner-release.yml` (zip and `SHA256SUMS`, tag `dbdatastore/v<v>`, called by `suite-release.yml`; no MSI or manifest of its own is required) | none | S | a dry-run of the suite release carries `dbdatastore-v<v>-win-x64.zip` and a verifying `SHA256SUMS`; the zip's `service install` and `server run` answer on a Windows runner |
| SC17 | **Status: built (2026-11-10) as the notes-say-so branch: no bun or package store is bundled, `dist.json` notes say a Workspace with no `apps.packageStore` (and bun) does not build; the Windows check is left for CI or the owner.** **The Workspace as a zip**, the same way (`DbDataWorkspace/dist.json`, service `DbDataWorkspace`), carrying what builds need or saying so: the pinned `bun` and the vendored package store location, else the notes say a Workspace with no `packageStore` does not build | none | S | as SC16; the zip builds the template app with the network blocked, or the notes say it cannot and a test checks the message |
| SC18 | **Build registers as a service in its package**: `DbDataBuild/dist.json` gets `service` (name `DbDataBuild`, the register command and the project folder it serves, a default under `%ProgramData%`), the MSI and Scoop route follow from it, and the suite lists it as a member | none | S | `dist/tests` checks the service block against the source like the other products; on Windows `service install` then `server run` answers its health check and the console lists it once paired |
| SC11 | **The suite manifest** (first cut: no `post_install` or `pre_uninstall`, notes with the manual commands): `dist/suite.json`, `dist.py bind --suite` writes `dbdatakit-full.json` (the members' zips as `url`/`hash` lists, `extract_to`, `bin` aliases), and `suite-release.yml` and `dist-republish.yml` publish it to the fixed `dbdatakit-full/dist*` releases | none | M | unit tests: every member's zip is listed with the hash from `SHA256SUMS`, a member with no win-x64 zip is refused naming it, a snapshot and a beta land in their own fixed release; a dry-run of the suite release carries the manifest **Status: first cut built, not yet wired into the release workflows** (2026-11-10). Built: `dist/suite.json`, `dist.py bind --suite` (`--sums` takes one SHA256SUMS per member or one for all; the action reads each member's own from its release), `.github/actions/publish-suite-dist`, tests in `dist/tests/test_suite.py`, the README section. **Not done:** the `suite-release.yml` job that calls the action (the coordinator adds it, with the Store and Workspace `dist.json` of SC16 and SC17: until both exist a real bind fails naming the member), `dist-republish.yml`, the dry-run check, and a run on a real Scoop (the multiple `url` with `extract_to` lists and `bin` as a list of pairs are Scoop's documented manifest shapes, not yet run here). |
| SC12 | **`post_install`**: global only, refuses on a member service from another folder, registers services, starts the console, pairs each member with a single-use code, starts them, prints the next steps; idempotent | SC11 | L | a Windows test runs the generated script against the built zips: every service is running and the console lists each member as ready; run twice, nothing is registered or paired again; a failure at one member names it and leaves the rest; no code appears in any output or file |
| SC13 | **Uninstall and update**: `pre_uninstall` unregisters and unpairs only on a real uninstall; an update stops, replaces and starts without unregistering or re-pairing | SC12 | M | a Windows test: after uninstall no member service exists and the console lists none, `%ProgramData%` settings remain; after an update to a second version the services run from the new files, still paired |
| SC14 | **The whole thing in CI on Windows**: install Scoop on the runner, install the suite manifest from the dry-run artifacts, update, uninstall, and a per-user install and an install over a registered service are refused | SC12, SC13 | M | `dist-msi-ci` (or a job of its own) runs it; this is the first test of Scoop's own `post_install`, `pre_uninstall` and update behaviour in this repository |
| SC15 | Docs: `dist/README.md` (the suite route, what it changes on the machine, one route per machine), the single-server install as one command, release notes | SC12 | S | `check_docs_commands.py` passes |
| SC30 | **Rename the repository folders**: `DbDataKit/` to `Shared/`, then `DbDataConsole/` to `DbDataKit/`; the product's projects and namespaces to `DbDataKit.Server*`; `DbDataKit.slnx`, every `.slnf`, `Directory.Build.targets` (how `DbDataKitReference` finds a library), the npm workspace path, Dockerfile contexts, the 15 workflows, `CLAUDE.md`, plans and docs; a script like `scripts/rename_proxy_to_gateway.py` with a test | none (a batch of its own, nothing else in flight) | L | a full build and every product's tests green on Windows and Linux; every `scripts/check_*.py`; the packages built before and after list the same files; `git log --follow` still shows a moved file's history |
| SC31 | **One data root and a folder per service**: `DBDATAKIT_DATA`, each service's default under it, the layout of `DirectoryLayout`, `globalEnv` and the notes of every `dist.json`, the MSI and Scoop defaults, the Linux units (`/var/lib/dbdatakit/<service>`), and a release note naming the old and new folders and the old service name | SC30 | M | each service started with no settings creates its folder under the root and nothing else; the per-product variable still overrides; `dist/tests` checks every `globalEnv` against the source |
| SC32 | **The service and the words**: the Windows service is named `DbDataKit` (`DbDataKitConsole` is gone, with no transition from it), the CLI help, screens, docs and plans say DbDataKit; a check that fails on `DbDataConsole` and `DbDataKitConsole` outside a baseline that only shrinks | SC30, SC31 | M | a Windows test installs the service and ends with one service named `DbDataKit`, started, paired; the check passes |
| SC20 | Build authoring (project kind, publishing as package and as `connection/2`) | SC05, plan 28c | placeholder | its own slice list, written when SC05 is built |
| SC21 | Store authoring (model project kind, approvals in the console) | SC05 | placeholder | as above |
| SC22 | Sync authoring (replication project kind, publishing the runtime package) | SC05, plan 28c | placeholder | as above |

**Order, quickest first (the testers' package):** SC11, SC16, SC17 and SC18 start (two agents at a time) (no two touch the same files except `suite-release.yml`, which one commit owns), then SC12, SC13, SC14 and SC15. That is the whole path to a `dbdatakit-full` package. In parallel and independent: SC01 to SC03 (Linux Gateway) and SC04 (the fold's security review). **Then the rename, alone:** SC30 in a batch of its own as soon as the packages are tested and nothing else is open (so it should come before SC05 to SC08, which edit the same files), then SC31 and SC32. **After it, in any order:** SC05 to SC08 (the Workspace into the console) and the authoring moves; each removes a member line. SC10 can run beside them. No more than two agents at a time.

## 9. Not verified

- Scoop's own behaviour: whether `pre_uninstall` also runs when Scoop replaces a version on `scoop update` (the script must tell an uninstall from an update, with the `$cmd` Scoop passes), whether a service can run from the `current` junction, and how `post_install` behaves on a failure. None of this has been run here; SC14 is the first test.
- That the members' default listen ports do not collide on one machine (each is the product's own default today); SC12's test starts them all together.
- The owner accepts the Linux Gateway limits below (2026-11-10).

- That the Gateway's Linux zip runs the ODBC paths: the unixODBC drivers and the Kerberos keytab route are documented and have never been run on a Linux install in this repository.
- That the Linux Dockerfile of the Gateway starts as published (the README says it has not been built or run in this environment; CI builds it).
- How much the console's package grows with `bun` and the vendored store.
- Whether the console's relay can pass the Workspace's preview and large archive calls unchanged (they are streamed today on the Workspace's own listener).
