# INSTALLER: install, startup and pairing

## Goal

Installing the suite on a Windows workstation, a Windows server, Linux or Docker Compose works the same way and fails in words. Each service creates and
owns all of its own data, starts through one shared sequence, writes a failed start to one known file, listens on a port of the suite range, and joins
the console by a single-use command the console generates. No pairing code is read out of a program's output or typed between services. The design
rules are in [docs/install.md](../docs/install.md).

## What exists today

- `dbdatakit-full` (Scoop, `dist/suite-scripts.ps1`, `dist/suite.json`) copies the programs to `%ProgramData%\DbDataKit\bin\<version>`, runs each
  member's `service install` elevated, pairs each member by `server pair` and `dbdatakit instance add --code`, and prints the setup link.
- The elevated `service install` creates the data folders, so they are owned by the administrator; the Gateway's `EnsureStarter` writes its configuration
  repository during install (the cause of the workstation failure `not owned by current user`).
- Only the Gateway writes a start failure (`StartFailure.cs`, `start-failure.txt` in its data folder).
- The Gateway's service is registered as `dbdatagateway`; the others as `DbDataKit`, `DbDataRunner`, `DbDataWorkspace`.
- Ports are fixed defaults: console 8480, identity home 8482, Workspace 8471, Runner 8495, Gateway 8815 to 8822 (`scripts/check_default_ports.py`).
- Pairing by code, everywhere: `server pair` and `dbdatakit instance add --code` (`ConsoleCli.cs`); `dbdatarunner package pair` and `package publish --code`
  (`RunnerCli.cs`); `dbdatabuild` publish to a Runner `--code` (`CliApp.cs`); `dbdatasync package publish --to runner --code` (`PackageCommand.cs`);
  `dbdatagateway apps publish --to --code` (`AppsCommand.cs`); `dbdataworkspace publisher pair --code` (`WorkspaceAdminCommand.cs`); `gateway add --code`
  of the Runner and of `dbdatastore` (`GatewayNoun` in `DbDataKit.Serving.Notify`, `StoreAdminCommand.cs`). Also announcement (`ConsoleAnnouncer`,
  `--announce-listen`, `DBDATAKIT_ANNOUNCE_URL`) and the bundle token (`bundle/1`).
- A second Runner instance joins its group by a code made on it (`POST /api/instances/{id}/join`); a Gateway group makes its own key.
- `doctor` is a verb of `service` (`HostingNouns.cs`, the Gateway's `Cli.cs` and `ServiceCli.cs`).
- Enterprise (AD CS) certificates, their renewal and rollover are the subject CERTIFICATES.
- Build's release and the Gateway's Windows build are self-contained; the other programs are framework-dependent.
- The Runner's Windows service job is written and has never run a task under the real service; no Runner release was cut with the release workflow.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| INSTALLER-1 | `doctor` becomes `service check` in the shared `ServiceCommand`, `HostingNouns` and the Gateway's `ServiceCli` and `Cli`; `service doctor` answers the sentence naming `service check` (`RemovedCommands`); a repository check fails on any command or subcommand named `doctor`; docs updated | none | S | the check's unit test fails on a command tree with a `doctor` verb and passes on the repository; the products' CLI tests call `service check`; `check_docs_commands.py` passes |
| INSTALLER-2 | The startup library `DbDataKit.Hosting.Startup`: `StartupSequence` (folder, keys, certificate, state, ports, join), the shared `logs` folder and its protection, `startup-failure-<service>.txt` ("not finished" line naming the step, deleted on success, the full reason on failure), the `SuiteServices` table | none | M | `DbDataKit.Hosting.Startup.Tests` on Linux and Windows CI: a clean sequence leaves no file; a failing step writes the account, the folders and the exception chain; a process killed in a step leaves "step: <name>, not finished"; on Windows a second service account cannot open the first's file; on Linux the file is mode 0600 |
| INSTALLER-3 | The four servers start through the sequence; the Gateway's `StartFailure.cs` and `start-failure.txt` go | INSTALLER-2 | M | each server's test starts it with its port held by another listener and finds `startup-failure-<service>.txt` with the account, the folder and the exception; a clean start leaves none; Linux and Windows CI |
| INSTALLER-4 | Names, accounts and descriptions from `SuiteServices`: `DbDataGateway` as `NT SERVICE\DbDataGateway`, the four exact descriptions; `dist.json` and the Scoop binding read the table | INSTALLER-2 | S | a shared test asserts the four names, accounts and descriptions; the Scoop e2e reads `sc qc` and `sc qdescription` for each |
| INSTALLER-5 | Reproduce the workstation on CI: a leg of the Windows install test with `nodefaultadminowner=1` and real-time scanning on, run once by hand on the code before INSTALLER-6, required after it | INSTALLER-4 | S | the dispatched run on the old code shows the Gateway's `not owned by current user` (linked in `docs/ci-lessons.md`); if it does not reproduce, the row in `docs/ci-lessons.md` says what differs and the owner's workstation is the check by hand (domain policy and security software are not on a hosted runner) |
| INSTALLER-6 | Services own their data: `service install`, the MSIs and `install.ps1 -InstallService` create only the top folder with its owner and access list; keys, certificate, state, configuration repository and `EnsureStarter` happen at first start; Linux units use `StateDirectory=` and `LogsDirectory=` | INSTALLER-3, INSTALLER-5 | M | the INSTALLER-5 leg passes; a test lists every file under each data folder after the first start and finds each owned by the service's account (Windows) or user (Linux, the server started with `sudo -u`) |
| INSTALLER-7 | Admin commands through the local API (`DbDataKit.Hosting.Local`) for all four servers: `secret`, `cert`, `state`, `setup link`, `user invite` call the running service; opening the files is `--recovery`, refused while the service runs | INSTALLER-6 | M | with the service running, `secret set` adds the value and no file under the data folder is owned by the administrator; a direct open while it runs is refused naming `--recovery`; recovery with the service stopped writes a file owned by the service's account; Windows leg and Linux job |
| INSTALLER-8 | `--skip-folder-permissions-check` and `startup.skip_folder_permissions_check`: accepted everywhere, a warning at every start, a line in `service check`, the server marked on the console's posture page | INSTALLER-3, INSTALLER-1 | S | tests: started as a service (detector replaced) and from a terminal it starts with the warning; without it the same folder fails the folder step naming the flag; `service check` and the posture list name the server |
| INSTALLER-9 | Ports of the console, Runner and Workspace from the suite range 7800 to 7899: a free port of the sub-block at first start, pinned in the server's own settings; excluded Windows ranges skipped; a configured port never moved; `check_default_ports.py` checks the sub-blocks | INSTALLER-3 | M | tests: the sub-block's first port held, the server takes the next and keeps it after a restart; a configured port held fails naming it; on the Windows runner an excluded range is skipped and, covering the whole sub-block, is named in the failure file; the repository check's unit test |
| INSTALLER-10 | The Gateway's listeners move into 7820 to 7839 with the ports ENDPOINTS-2 fixes | INSTALLER-9, ENDPOINTS-2 | S | the Gateway's tests start it with no configured port and find each listener inside its sub-block, pinned after a restart; `check_default_ports.py` passes |
| INSTALLER-11 | The join library in `DbDataKit.Web`: the single-use token (hash, scope, expiry, five tries), the join request and answer, the proofs, the console's callback, the pins both ways, the `join-request/1` file | INSTALLER-2 | M | `DbDataKit.Web.Tests`: a join in one process succeeds once; a spent, expired, other-product or other-group token, a wrong thumbprint and a callback to an address that does not answer with the fingerprint are each refused and audited with no token value |
| INSTALLER-12 | Reversed pairing: the console's `POST /api/v1/join`, `dbdatakit setup invite <gateway\|runner\|workspace> [--role] [--group]`, `pair --host --token --console-thumbprint [--address]` in the Gateway, Runner and Workspace, the join step at start; `docs/architecture.md` D14 and the Web README say a server calls the console once, to join | INSTALLER-11, INSTALLER-9 | L | Servers suite (SQLite, PostgreSQL, SQL Server): a console and a real Gateway, Runner and Workspace process each joined by its printed command and listed ready with the address and fingerprint they reported; a reused token is refused; the console's calls succeed pinned |
| INSTALLER-13 | The console's Add a server screen: product, role ceiling, group, the command to copy, the waiting tokens with Cancel | INSTALLER-12 | S | Playwright story: make a command, run it against a test Runner, the server appears ready; vitest of the form |
| INSTALLER-14 | `dbdatakit setup single-server` and `--stop`: discovery, registration by library call, folder and program access lists, tokens, start, wait, per-service progress, failure files, summary, safe to run again | INSTALLER-12, INSTALLER-6 | L | Windows CI (zips extracted, no Scoop) and the Linux job (systemd): every service registered, joined and listed; a second run says nothing to do; a service made to fail prints its failure file and the summary names it; a run after the fix finishes it |
| INSTALLER-15 | Groups through the token: the console makes every group key (a Gateway group's too) and hands it at the join; the join answer carries a link when TRUSTZONES-1 made one | INSTALLER-12 | M | Servers suite: two Gateway instances joined to group X serve each other's sessions with no key copied; a Runner instance joined to group Y resolves the group's logins; a token for Y used by a Gateway, or for another group, is refused |
| INSTALLER-16 | Server-to-server and publisher trust through the console: the Workspace's publisher keys on a Gateway or a Runner, and a person's publisher client for the command lines (`dbdatarunner package publish`, `dbdatabuild` and `dbdatasync` publish to a Runner, `dbdatagateway apps publish --to`), are registered by the console (INSTALLER-P3); every `--code` of those commands answers the sentence | INSTALLER-12 | M | the Workspace publishes to a Gateway and a Runner with no code typed; each command line publishes with a client the console registered; each removed `--code` form answers its sentence (one test per command) |
| INSTALLER-17 | The Scoop scripts shrink (`docs/install.md`, "Scoop"): no copy; `post_install` prints the next commands; `pre_uninstall` stops the services and on an uninstall prints the `sc.exe delete` commands; verify which `pre_uninstall` runs during `scoop update` (the installed version's) and whether Scoop's running-process check comes before it, with and without `ignore_running_processes`; if both allow it, `pre_uninstall` stops the services on an update too; the outcome in `docs/ci-lessons.md` (its row on "Running process detected, skip updating" rewritten); `dist/README.md` and `docs/suite-single-server-install.md` rewritten | INSTALLER-14 | M | `suite-scoop-e2e.ps1`: install, `dbdatakit setup single-server`, every service ready and registered against `current`; the update check installs a version N whose manifest carries `pre_uninstall`, then updates to N+1 (a new manifest alone cannot show it), and records which `pre_uninstall` ran and whether the running-process check came first, with and without `ignore_running_processes`; the update from N to N+1 by the verified procedure (one command if both allow it, otherwise `setup single-server --stop`, `scoop update`, `setup single-server`) ends with the new version running; uninstall leaves no service running |
| INSTALLER-18 | Docker Compose through the same path: the console writes one `join-request.yaml` per member into a shared volume | INSTALLER-14 | S | `dist/compose/check.sh` on the quickstart: the console and the Gateway are joined with no `instance confirm`; CI runs it |
| INSTALLER-19 | Clean break messages, one per row of the table in `docs/install.md` ("Clean break"): a file in a service's top folder owned by another account (the folder step), a `server.yaml` listing a console paired the old way (a warning at start and in `service check`; the pairing not used), old `instances.yaml` entries (removed by `setup single-server`, marked on the Servers page with the generated command), a `dbdatagateway` registration (stopped, removed, `DbDataGateway` registered on the same folder), services registered from `bin\<version>` (registered against the program folder, the copy removed), `bundle.json` and `--announce-listen` (the console refuses to start), `DBDATAKIT_ANNOUNCE_URL` (a warning at every start), the old default ports (the release notes, `docs/suite-single-server-install.md` and the Servers page), `service doctor`, `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` (exit 2, `RemovedCommands`) | INSTALLER-14, INSTALLER-15 | S | one test per row of the table: each finding gives its message (naming what the table's last column says), exit code or warning, from the one the table names as saying it; the old-port row is checked by `check_docs_commands.py` and a test that the Servers page shows the reported address |
| INSTALLER-20 | Delete the old pairing and install code: `PairingCodes`, `pairing-codes.json`, the code verbs, `PairingClient`'s code exchange, the announcement, the bundle (`bundle/1` leaves `docs/formats.json` and `docs/ui-editors.md`), the group join route, `publisher pair --code`. Voids: the Gateway pairing of the Store command line (`dbdatastore gateway add`, with PUBLISHING-2) | INSTALLER-13 to INSTALLER-19 | M | the suites pass with those types gone; the removed verbs answer their sentence; `check_formats.py` and `check_ui_editors.py` pass without `bundle` |
| INSTALLER-21 | A service writes its own `state:` settings: at first start from the settings `setup single-server` or the MSI pass (`STATE_ENGINE`, `STATE_CONNECTION` with a secret reference), never a password; one login per service and group | INSTALLER-14, TRUSTZONES-2 | M | Windows leg: an MSI install with the two properties starts the service on SQL Server with the login it names; Linux: `install.sh` the same on PostgreSQL; a password in the property is refused |
| INSTALLER-22 | The Runner's release and Windows service legs: a release cut with `dbdatarunner-release.yml`, the MSI installed on Windows, a task run under `NT SERVICE\DbDataRunner`; a package's manifest generated by a build step (`ManifestGenerator.ForAssembly`) | INSTALLER-6 | M | the `windows` job of `dbdatarunner-ci.yml` installs the service, runs a task, reads its run and removes the service; a snapshot release publishes the Runner's zips and MSI |
| INSTALLER-23 | Framework-dependent Build and Gateway: `scripts/publish.sh`, `build/publish.ps1`, `package.ps1`, `requiresDotnet` in `dist.json`; the 32-bit ODBC worker stays self-contained | none | M | `dist/tests` and the suite dry run pass with every zip framework-dependent but the worker; `dbdatabuild version` and a Gateway query run on a machine with only the .NET 10 runtime; the zip sizes are in the commit |
| INSTALLER-25 | First-run remainders: `DbDataKit/docs/scale-out.md` rewritten for joins by token (state move, another Gateway instance, a server on another machine); the Gateway and the Runner still install, start and join alone from their own MSI | INSTALLER-14 | S | `ConsoleScaleOutTests` follow the document's commands; the per-product MSI jobs (`dist-msi-ci.yml`) pass with a join by token |
| INSTALLER-26 | Container images for the Runner and the Workspace, so the Compose quickstart runs all four services (queued: wanted, not yet) | INSTALLER-18 | M | `dist/compose/check.sh` runs four containers joined, and a Build refresh task runs on the Runner's container |
| INSTALLER-27 | Console-started pairing for networks where a server cannot reach the console (low priority): the console connects to the server, which shows the generated code | INSTALLER-12, INSTALLER-13 | M | Servers suite: a Runner whose calls to the console are blocked joins by this path and is listed ready; a reused or expired code is refused; a Playwright story of entering the address |
| INSTALLER-28 | Serving groups upgrade rolling, draining first (waits for the first stable release) | TASKENGINE-9 | L | the servers suite upgrades a two-instance group with a query running; no query fails; the second instance waits for the first |
| INSTALLER-29 | The compatibility job runs the previous release's programs against the current build (waits for the first stable release); the release workflows write `release.json` with the compatibility generation, so the console's "outside N-1" mark shows for a release from GitHub, not only from a mirror | none | M | the `compatibility` job pairs the previous stable console with the current Gateway and Runner and the reverse, and a publish and a query pass; a snapshot release carries `release.json` with its generation and the console's Upgrades page marks a release outside N-1 from it |

## Needs from other subjects

- ENDPOINTS-2: the Gateway's listeners and their default ports, before INSTALLER-10 pins them.
- TRUSTZONES-2: a state login per service and group, before INSTALLER-21 writes it.
- TRUSTZONES-1: the link the join answer carries (INSTALLER-15 carries it when it exists).
- TASKENGINE-9: two Gateway instances behind a balancer in CI, before INSTALLER-28.
- PUBLISHING-2: the Store command line's `gateway add` goes with `dbdatastore model publish --proxy`.

## Proposed decisions

### INSTALLER-P1: defaults taken while planning plan 36 (default, awaiting the owner)

These were taken by the agent that wrote the old plan; the owner was not asked. Each is how the task is built unless the owner says otherwise.

1. The library is `Shared/src/DbDataKit.Hosting.Startup`; the join lives in `DbDataKit.Web`.
2. One table `SuiteServices` (name, account, description, program, data and log folder, port sub-block, start order) is the only place a service name is spelled.
3. `<service>` in log folders and failure files is the program name in lower case (`dbdatagateway`), the same as the systemd unit.
4. The "not finished" line names the step: `starting since 2026-11-17T09:12:03Z, step: certificate, not finished`.
5. The shared `logs` folder: on Windows Administrators and SYSTEM full control, each service account "create files" and "create folders" on that folder only,
   CREATOR OWNER full control; on Linux `root:dbdatakit` mode `1770`, files `0600`. A service that finds its own subfolder or failure file owned by
   another account writes nothing there and says so in the event log.
6. The install sets the top folder's owner to the Administrators group on every Windows edition and grants the service account Modify.
7. A free port is found by binding; an excluded port refuses with WSAEACCES and is skipped like a taken one; the failure file lists the excluded
   ranges when the whole sub-block is unusable. The identity home, running on its own, keeps a port in the console's sub-block.
8. The join token is 128 random bits, shown once, ends in 15 minutes (`--minutes` up to 24 hours), spent when the console's callback confirms.
9. `pair` writes one file, `join-request.yaml` (`join-request/1`), into the service's top folder; the service reads and deletes it at its next start.
10. Docker Compose passes tokens through a shared volume, not environment variables.
11. A server calls the console exactly once, to join; every other call is the console calling the server.

Scenario: an administrator runs `dbdatarunner pair --host https://console:7800 --token ... --console-thumbprint sha256:...` on a new machine. Under these
defaults the command writes `C:\ProgramData\DbDataKit\runner\join-request.yaml`, restarts the service, the service joins as `NT SERVICE\DbDataRunner`,
deletes the file and logs to `C:\ProgramData\DbDataKit\logs\dbdatarunner\`. Recommendation: accept all eleven.

### INSTALLER-P2: the Gateway's ports once ENDPOINTS has consolidated its listeners (awaiting the owner)

`docs/install.md` ("Ports") gives the Gateway 7820 to 7839 with no fixed split between its listeners. With ENDPOINTS the Gateway has three listeners by default (public
HTTPS, Flight SQL, management). Option A: free ports of the sub-block, pinned at first start (a client learns them from the console's Servers page).
Option B: fixed defaults 7820 public, 7821 Flight SQL, 7822 management, still pinned and moved only if taken. Scenario: a BI user sets up an ODBC DSN
for Flight SQL; under A the port differs per machine and is read from the console, under B it is 7821 on every default install. Recommendation: B (see
ENDPOINTS-P1).

### INSTALLER-P3: how a person's command line publishes without a code (awaiting the owner)

Today `dbdatarunner package publish --code` pairs the person's machine with a Runner. With every code gone: option A, `dbdatakit setup invite publisher
--target <runner|gateway> --role publisher` prints `<program> client join --host ... --token ...`, which makes a client key on the person's machine and the
console registers it with the target; option B, command lines stop publishing and only the Workspace publishes. Scenario: a developer runs
`dbdatabuild project publish --to runner` from a laptop; under A it works after one `client join`, under B it answers "publish from the Workspace".
Recommendation: A, so the command lines stay usable without the Workspace.

## Shared areas touched

- New `Shared/src/DbDataKit.Hosting.Startup`; `DbDataKit.Web` (pairing, join), `DbDataKit.Hosting.Cli` (`ServiceCommand`, `HostingNouns`),
  `DbDataKit.Hosting.WindowsService`, `DbDataKit.Hosting.Local`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Serving.Notify` (pairing verbs).
- `dist/` (suite scripts, `dist.json` files, compose), `scripts/check_default_ports.py`, a new command-name check under `scripts/`.
- `docs/formats.json` (`join-request/1` added, `bundle/1` removed), `docs/ui-editors.md`, `docs/architecture.md` (D14), `docs/ci-lessons.md`,
  `docs/release-notes-pending.md`, `DbDataKit.slnx` (the new project).
