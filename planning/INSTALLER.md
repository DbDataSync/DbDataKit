# INSTALLER: install, startup and pairing

## Goal

Installing the suite on a Windows workstation, a Windows server, Linux or Docker Compose works the same way and fails in words. Each service creates and
owns all of its own data, starts through one shared sequence, writes a failed start to one known file, listens on a port of the suite range, and joins
the console by a single-use command the console generates. No pairing code is read out of a program's output or typed between services. The design
rules are in [docs/install.md](../docs/install.md).

## What exists today

- `dbdatakit-full` (Scoop, `dist/suite-scripts.ps1`, `dist/suite.json`) copies the programs to `%ProgramData%\DbDataKit\bin\<version>`, runs each
  member's `service install` elevated, pairs each member by `server pair` and `dbdatakit instance add --code`, and prints the setup link.
- Services own their data (`docs/install.md`, "A service owns its data"): `service install` of the four servers (run by the MSIs, `install.ps1
  -InstallService` and the Scoop package) creates only the top folder, owned by the Administrators group, with its access list; the Gateway writes its
  starter `config/server.yaml` at its first start; a configuration repository git refuses for its owner stops the Gateway's start naming the folder, its
  owner and the fix (no allow list). On Linux `install.sh --system` makes the service's system user (named as the program, group `dbdatakit`) and its
  unit, which runs as that user with `StateDirectory=` and `LogsDirectory=dbdatakit/<program>` (mode 0700), and the failure file is
  `/var/log/dbdatakit/<program>/startup-failure-<program>.txt`. The Scoop package starts each member before `server pair` writes in its folder.
  `suite-install-windows` (both legs) and `suite-install-linux` (`dist/tests/suite-systemd-e2e.sh`) of a dry run check it.
- The four servers' `server run` start through the startup library `Shared/src/DbDataKit.Hosting.Startup`: a failed start writes
  `startup-failure-<program>.txt` (an install's data folder: the shared `logs` folder beside it; any other data folder keeps them with its data) and the Application
  event log for a Windows service, says the step and the file on standard error, and exits non-zero; a port that cannot be bound fails the ports step
  naming the port and Windows' excluded ranges; `service check` prints the file. Each server fills in the folder, certificate, state and ports steps
  with the work its start already did; the keys and join steps are empty. The library: `StartupSequence` (the six steps in order, a failed
  step stops the start and returns exit code 1), `StartupStepException` (what each step's failure names), `StartupPorts` (a refused listener as the ports step's failure), `startup-failure-<program>.txt` (the
  progress line, the full reason, deleted after a clean start; 0600 on Linux, its account, SYSTEM and Administrators on Windows), the service's own
  logs subfolder, `SharedLogsFolder` (the shared folder's protection) and `SuiteServices` (program, Windows name and account, description, data
  folder, start order, and the suite range and port sub-blocks).
- The four servers' `service install` register `DbDataKit`, `DbDataGateway`, `DbDataRunner` and `DbDataWorkspace`, each as `NT SERVICE\<name>`, with
  the description of `SuiteServices` (also in their systemd units); `dist.py` and `msi.py` read the names from the table (`dist/suite_services.py`),
  and the Scoop e2e reads them back with `sc qc` and `sc qdescription` (`docs/install.md`, "Service names, accounts and descriptions").
- Ports: the console, Runner and Workspace take their port from `--listen`, else the `listen:` line of their settings file, else the first free port of their sub-block of 7800 to 7899 (defaults 7800, 7840, 7860), which they write as that line once they listen on it (`ListenPorts` in the startup library; `docs/install.md`, "Ports"); the identity home on its own defaults to 7819; the Gateway's public, Flight SQL and management listeners take free ports of 7820 to 7839 and pin them as `listen` settings. `scripts/check_default_ports.py` checks the range, the sub-blocks, the defaults and Windows' default ports. A bare `health check` (`HealthNoun`) asks the address of the server's `listen:` line (`--repo`, or the default folder), else the default address. The console's and the Gateway's images and the compose files of `dist/compose` use the suite's ports (the console 7800, the Gateway's Flight SQL 7821 and management 7822), and both images' health checks are a bare `health check`.
- Pairing by code, everywhere: `server pair` and `dbdatakit instance add --code` (`ConsoleCli.cs`); `dbdatagateway apps instance-save|list|rollback|approve --to --code`
  (`AppsCommand.cs`); `dbdataworkspace publisher pair --code` (`WorkspaceAdminCommand.cs`); `gateway add --code`
  of the Runner and of `dbdatastore` (`GatewayNoun` in `DbDataKit.Serving.Notify`, `StoreAdminCommand.cs`). Also announcement (`ConsoleAnnouncer`,
  `--announce-listen`, `DBDATAKIT_ANNOUNCE_URL`) and the bundle token (`bundle/1`).
- The join library in `DbDataKit.Web` (`Shared/src/DbDataKit.Web/README.md`, "Joining the console"): `JoinTokens` (128 random bits, scope, 15
  minutes by default and at most 24 hours, five refused tries; the console keeps a verifier made from the token), `JoinService` (the console's checks, the
  callback, the audit `server.join` with who made the token and never its value), `JoinClient` (the join over TLS pinned to the console's
  thumbprint, signed with a key derived from the token), `JoinEndpoint` (`POST /api/v1/join`), `JoinCallbackEndpoint` and `PendingJoins` (the
  server's answer to the callback), `JoinRequestFile` (`join-request/1`).
- Reversed pairing is built beside pairing by code: `dbdatakit setup invite <gateway|runner|workspace> [--role] [--group] [--minutes] [--url]` makes a
  join token (a row of the `join_token` table of the console's `trust` state module, `StateJoinTokenStore`) and prints the server's command; the
  console's `POST /api/v1/join` is outside its sign-in (`ConsoleJoins`) and records a joined server in `instances.yaml` with its certificate pinned;
  `pair --host --token --console-thumbprint [--address] [--repo]` of the Gateway, Runner and Workspace (`JoinPairCommand`, a one-word command of the
  shared router) writes the request and restarts the registered service; their join step (`ServerJoin`) joins while the listener answers the
  callback, keeps the console as a paired client and its thumbprint in the group's settings (`JoinedConsoles`), and a refused join fails the start at
  the join step (`StartupStepException.JoinFailed`). The Servers suite joins a real Gateway, Runner and Workspace on SQLite, SQL Server and PostgreSQL
  (`JoinByTokenTests`).
- The console's Add a server screen (`/add-server`, an administrators' entry of the Servers group; `AddServerPage.tsx`) makes the same token as
  `setup invite` through `POST /api/join-tokens` (the server, the role ceiling, a group, the minutes and the console's address, which starts as the
  browser's), shows the command once with Copy, and lists the waiting tokens (`GET /api/join-tokens`, never a value) with Cancel (`DELETE
  /api/join-tokens/<id>`, audited as `server.invite.cancel`). The story `join-by-command.spec.ts` joins a real Runner by the command it makes.
- `dbdatakit setup single-server` registers, starts and joins every suite service installed beside `dbdatakit` (`SingleServerSetup` in
  `DbDataKit.Hosting.Cli`, the console's half `ConsoleSingleServer`): it finds the programs in the folders next to the console's (named for the service, as
  the zips and the Scoop package lay them out, or for the program, as `install.sh` does), registers each by calling the registration every
  server's own `service install` and `service unit` now use (`SuiteServiceRegistration`), gives its top folder its owner and access list and its program
  folder an access list only administrators can change, writes a join request with a single-use token,
  starts it, waits for it to listen and to be listed by the console, prints `registered · started · listening · joined` per service and
  the startup-failure file of one that fails, and ends with what is ready, what is not, the console's address and the setup link; a second run leaves
  what is ready alone. On Windows it uses the service control manager (`WindowsSuiteServiceHost`), on Linux systemd
  (`SystemdSuiteServiceHost`: the system user, the unit, enabled). `--skip-folder-permissions-check` goes on each registered command
  line; `--stop` stops every suite service of the machine. Tested with fakes (`DbDataKit.Hosting.Cli.Tests`), with the real programs in the Servers suite
  (`SingleServerSetupTests`), and in the suite dry run on Windows from the extracted zips (`suite-single-server-windows`,
  `dist/tests/suite-single-server-e2e.ps1`) and on systemd (`suite-install-linux`, `dist/tests/suite-single-server-e2e.sh`); their first dry run
  found two faults, both fixed (`docs/ci-lessons.md`, "Windows services"). The Scoop package does not call it yet (INSTALLER-17).
- The console's publisher key (`docs/install.md`, "The console's publisher key"): `ConsolePublisherKeyStore` keeps an Ed25519 key of the console's own in its
  secret store (`dbdatakit:server:identity:publisher-key`, apart from the key that signs its tokens); `ConsoleTrust` reads the kinds a
  server takes (`GET /api/v1/trust`, a new read of the shared `DefinitionsApi`, mapped on the Runner and served by the Gateway's management API) and adds the key for each kind that does not list it (`PUT /api/v1/trust/{kind}/keys`, labelled `dbdatakit console <console id>`), as
  the console itself (`ConsoleRelay.CallAsConsoleAsync`: a token of kind `console`, the administrator role within the ceiling);
  `ConsoleTrustService` runs it over every registered Runner and Gateway 15 seconds after the console starts, at once after a join, and again for a
  server that did not answer (2 s, 5 s, 15 s, 1 min, 5 min, then every 10 min). A revoked key is not added again; a refusal is in the console's audit
  (`server.trust`). Tests: `ConsoleTrustTests` (an in-process Runner and a Gateway's trust calls; a version the console signs verifies on the Runner
  with signatures required), `JoinByTokenTests` (the real Runner and Gateway trust the key after their join, and a signed version verifies on the
  Runner), `DefinitionsApiTests`, `PublishTrustTests`, `ManagementDefinitionsTests`.
- Command lines that no longer publish answer `RemovedCommands.ConsoleDeploys` (`DbDataKit.Cli`; INSTALLER-33 changes the wording) and exit 2:
  `dbdatarunner package pair` and `package publish` (`PublishApiTests` publish with `RunnerPublishClient`); `dbdatasync package publish --to runner`
  with `--server` or `--code` (`--out` and `--check` stay; the real-runner staging tests and the Servers suite's `SyncOnRunner` write the package with
  `--out` and send it with `RunnerPublishClient`); `dbdatabuild project publish` with `--server`, `--code`, `--promote-from`, `--channel`, `--bind`,
  `--serve`, `--expected-version` or `--client-dir` (`--to runner --out` stays; `ProjectPublishOnRunnerTests` and `BuildOperationsOnRunnerTests` write
  the package with `--out` and send it with `RunnerPublishClient`); `dbdatagateway apps publish --to` (`apps publish --dir` stays; `AppsRemoteTests`
  send an app version with `AppsRemote`). One test per command: `RunnerCliTests`, `PackageCommandTests`, `ProjectPublishTests`, `AppsRemoteTests`.
  The Playwright stories `runner.spec.ts`, `runner-schedule.spec.ts` and `runner-checkpoints.spec.ts` publish the sample package through the console's
  relay as the signed-in administrator (`tests/runner-publish.ts`). Nothing writes Sync's published marker (`PublishedDefinition.Record`) any more; only the removed form did.
- A second Runner instance joins its group by a code made on it (`POST /api/instances/{id}/join`); a Gateway group makes its own key.
- The folder step of the four servers checks the data folder (`FolderPermissions` in the startup library) and fails
  naming the folder, its owner, what is wrong, the flag and the setting. `--skip-folder-permissions-check` on `server run` and
  `startup.skip_folder_permissions_check: true` in the server's settings file skip it, with a warning on standard error at every start (and the
  Application event log for a Windows service); `service install --skip-folder-permissions-check` puts the flag on the registered command line
 ; `service check` has a line `startup.folder-permissions` naming the service and the switch; each server lists the skipped check in
  `loosened` of its `/version` answer, and the console's Bindings page names each server that skips it under "Posture choices". `dbdatakit setup single-server` passes the flag on to the services it registers.
- `service check` checks a service's registration, account and folder permissions in every program and prints its startup-failure file; the verb's
  earlier name is gone from every command, type, test, doc and plan, and `scripts/check_docs_commands.py` (`FORBIDDEN_WORDS`) fails when a tracked file
  uses it again.
- Enterprise (AD CS) certificates, their renewal and rollover are the subject CERTIFICATES.
- Build's release and the Gateway's Windows build are self-contained; the other programs are framework-dependent.
- The Runner's Windows service job is written and has never run a task under the real service; no Runner release was cut with the release workflow.
- The Windows install test (`suite-install-windows` of `suite-release.yml`, `dist/tests/suite-scoop-e2e.ps1`) has a leg `workstation`, run in every dry
  run: the install runs with a Windows client's owner (`nodefaultadminowner=1`, and the same default owner on the test's process token), so what the
  administrator makes is owned by that user, and the runner's `safe.directory=*` is removed, so git checks the owner of the Gateway's repository as on a
  client. Both legs print the owner of everything under the data folder (`docs/ci-lessons.md`).

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| INSTALLER-7 | Admin commands through the local API (`DbDataKit.Hosting.Local`) for all four servers: `secret`, `cert`, `state`, `setup link`, `user invite` and the rest call the running service; opening the files is `--recovery`, refused while the service runs, and a file it writes is given the service's account as owner; `WindowsServiceAccounts`' accepted grants (`e0135708`, which taught the command line which service accounts a key file may grant) are then needed only in recovery; `setup single-server` mints its join tokens through the local channel, so the administrator opens no file in the console's folder | none | M | with the service running, `secret set` adds the value and no file under the data folder is owned by the administrator; a direct open while it runs is refused naming `--recovery`; recovery with the service stopped writes a file owned by the service's account; Windows leg and Linux job |
| INSTALLER-15 | Groups through the token: the console makes every group key (a Gateway group's too) and hands it at the join; the join answer carries a link when TRUSTZONES-1 made one; a Gateway joining an existing group gets the group's pinned ports in the join answer and they become its exact configuration (a taken one fails the ports step naming it; another is never picked); the Gateway's pinned `listen`, `public.listen` and `management.listen` in `config/server.yaml` are the settings handed over | none | M | Servers suite: two Gateway instances joined to group X serve each other's sessions with no key copied; the second instance listens on exactly the first's pinned ports; with one of them held on the second's machine, the second fails with a startup-failure file naming the port and listens on nothing; a Runner instance joined to group Y resolves the group's logins; a token for Y used by a Gateway, or for another group, is refused |
| INSTALLER-17 | The Scoop scripts shrink (`docs/install.md`, "Scoop"): no copy; `post_install` prints the next commands; `pre_uninstall` stops the services and on an uninstall prints the `sc.exe delete` commands; verify which `pre_uninstall` runs during `scoop update` (the installed version's) and whether Scoop's running-process check comes before it, with and without `ignore_running_processes`; if both allow it, `pre_uninstall` stops the services on an update too; the outcome in `docs/ci-lessons.md` (its row on "Running process detected, skip updating" rewritten); `dist/README.md` and `docs/suite-single-server-install.md` rewritten | none | M | `suite-scoop-e2e.ps1`: install, `dbdatakit setup single-server`, every service ready and registered against `current`; the update check installs a version N whose manifest carries `pre_uninstall`, then updates to N+1 (a new manifest alone cannot show it), and records which `pre_uninstall` ran and whether the running-process check came first, with and without `ignore_running_processes`; the update from N to N+1 by the verified procedure (one command if both allow it, otherwise `setup single-server --stop`, `scoop update`, `setup single-server`) ends with the new version running; uninstall leaves no service running |
| INSTALLER-18 | Docker Compose through the same path: the console's settings list its members (`setup.members`); at its first start it writes one `join-request.yaml` per member into the shared volume `join`; each member's container consumes its own at its start; the quickstart loses `instance pending` and `instance confirm` | none | S | `dist/compose/check.sh` on the quickstart: the console and the Gateway are joined with no `instance confirm`; CI runs it |
| INSTALLER-19 | Clean break messages, one per row of the table in `docs/install.md` ("Clean break"): a file in a service's top folder owned by another account (the folder step), a `server.yaml` listing a console paired the old way (a warning at start and in `service check`; the pairing not used), old `instances.yaml` entries (removed by `setup single-server`, marked on the Servers page with the generated command), a `dbdatagateway` registration (stopped, removed, `DbDataGateway` registered on the same folder), services registered from `bin\<version>` (registered against the program folder, the copy removed), `bundle.json` and `--announce-listen` (the console refuses to start), `DBDATAKIT_ANNOUNCE_URL` (a warning at every start), the old default ports (the release notes, `docs/suite-single-server-install.md` and the Servers page), `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` (exit 2, `RemovedCommands`) | INSTALLER-15 | S | one test per row of the table: each finding gives its message (naming what the table's last column says), exit code or warning, from the one the table names as saying it; the old-port row is checked by `check_docs_commands.py` and a test that the Servers page shows the reported address |
| INSTALLER-20 | Delete the old pairing and install code: `PairingCodes`, `pairing-codes.json`, the code verbs (`server pair`, `instance add --code`), `PairingClient`'s code exchange, the announcement, the bundle (`bundle/1` leaves `docs/formats.json` and `docs/ui-editors.md`), the group join route, `publisher pair --code`. Voids: the Gateway pairing of the Store command line (`dbdatastore gateway add`, with PUBLISHING-2) | INSTALLER-15 to INSTALLER-19 | M | the suites pass with those types gone; the removed verbs answer their sentence; `check_formats.py` and `check_ui_editors.py` pass without `bundle` |
| INSTALLER-21 | A service writes its own `state:` settings: at first start from the settings `setup single-server` or the MSI pass (`STATE_ENGINE`, `STATE_CONNECTION` with a secret reference), never a password; one login per service and group | none | M | Windows leg: an MSI install with the two properties starts the service on SQL Server with the login it names; Linux: `install.sh` the same on PostgreSQL; a password in the property is refused |
| INSTALLER-22 | The Runner's release and Windows service legs: a release cut with `dbdatarunner-release.yml`, the MSI installed on Windows, a task run under `NT SERVICE\DbDataRunner`; a package's manifest generated by a build step (`ManifestGenerator.ForAssembly`) | none | M | the `windows` job of `dbdatarunner-ci.yml` installs the service, runs a task, reads its run and removes the service; a snapshot release publishes the Runner's zips and MSI |
| INSTALLER-23 | Framework-dependent Build and Gateway: `scripts/publish.sh`, `build/publish.ps1`, `package.ps1`, `requiresDotnet` in `dist.json`; the 32-bit ODBC worker stays self-contained | none | M | `dist/tests` and the suite dry run pass with every zip framework-dependent but the worker; `dbdatabuild version` and a Gateway query run on a machine with only the .NET 10 runtime; the zip sizes are in the commit |
| INSTALLER-25 | First-run remainders: `DbDataKit/docs/scale-out.md` rewritten for joins by token (state move, another Gateway instance, a server on another machine); the Gateway and the Runner still install, start and join alone from their own MSI | none | S | `ConsoleScaleOutTests` follow the document's commands; the per-product MSI jobs (`dist-msi-ci.yml`) pass with a join by token |
| INSTALLER-26 | Container images for the Runner and the Workspace, so the Compose quickstart runs all four services (queued: wanted, not yet) | INSTALLER-18 | M | `dist/compose/check.sh` runs four containers joined, and a Build refresh task runs on the Runner's container |
| INSTALLER-27 | Console-started pairing for networks where a server cannot reach the console (low priority): `setup invite` and the Add a server screen offer it; the console still generates the command for the remote, code included; it then either takes the remote's address up front, or waits for the user to run the command there and enter the address the command prints; it then connects to the server and pins it, with the token's scope and single use | none | M | Servers suite: a Runner whose calls to the console are blocked joins by this path and is listed ready, once with its address given up front and once with the address the command printed entered afterwards; a reused or expired code is refused; a Playwright story of entering the address |
| INSTALLER-28 | Serving groups upgrade rolling, draining first (waits for the first stable release) | TASKENGINE-9 | L | the servers suite upgrades a two-instance group with a query running; no query fails; the second instance waits for the first |
| INSTALLER-29 | The compatibility job runs the previous release's programs against the current build (waits for the first stable release); the release workflows write `release.json` with the compatibility generation, so the console's "outside N-1" mark shows for a release from GitHub, not only from a mirror | none | M | the `compatibility` job pairs the previous stable console with the current Gateway and Runner and the reverse, and a publish and a query pass; a snapshot release carries `release.json` with its generation and the console's Upgrades page marks a release outside N-1 from it |
| INSTALLER-33 | The messages of the removed ways to publish (`dbdatasync package publish --to runner --server` and the others) name the Workspace's publish panel in the console, and the Runner's Packages screen for a package folder | none | S | the command-line tests check the new sentence |
| INSTALLER-34 | An installed suite as a test fixture (not urgent; manual tests from a snapshot release meanwhile): a CI job on Linux installs the packaged suite with `install.sh`, runs `dbdatakit setup single-server`, enrols the first administrator from the setup link in `FIRST-RUN.txt` with a virtual passkey authenticator (as `tests/people.ts` does) and saves the signed-in browser state, the console's address and the testenv engines' addresses; any later story starts from that state instead of starting services from their build folders. Linux first; Windows (the MSI) later | none | M | the job installs, joins all four services, signs in, and a trivial story reusing the saved state opens the Servers page with four services joined |
| INSTALLER-36 | The examples and defaults that still name the old ports use the new ones: the console's `--announce-listen` message (7801), the address examples in the console's instance and group-key errors (a Runner at 7840), `dist/install.ps1`'s announce address and `dist/tests/msi-announce-e2e.ps1`'s default ports (7800, 7801). Test fixtures whose address is only an example stay as they are | none | S | the console's server tests and `dist/tests` pass, and no message, script or doc example outside test fixtures names 8480, 8481 or 8816 |

## Proposed decisions

## Needs from other subjects

- INSTALLER-21 writes each service's `state:` section by `state init --topology install` (`docs/state-logins.md`): the login `dbdatakit_<service>[_<group>]`, its schemas and registry, and the statements `state grants` prints exist.
- TRUSTZONES-1: the link the join answer carries (INSTALLER-15 carries it when it exists).
- TASKENGINE-9: two Gateway instances behind a balancer in CI, before INSTALLER-28.
- PUBLISHING-2: the Store command line's `gateway add` goes with `dbdatastore model publish --proxy`.

## Shared areas touched

- New `Shared/src/DbDataKit.Hosting.Startup`; `DbDataKit.Web` (pairing, join), `DbDataKit.Hosting.Cli` (`ServiceCommand`, `HostingNouns`),
  `DbDataKit.Hosting.WindowsService`, `DbDataKit.Hosting.Local`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Serving.Notify` (pairing verbs).
- `dist/` (suite scripts, `dist.json` files, compose), `scripts/check_default_ports.py`.
- `docs/formats.json` (`join-request/1` added, the entry is in place; `bundle/1` removed), `docs/ci-lessons.md`,
  `docs/release-notes-pending.md`, `DbDataKit.slnx` (the new project).
- `docs/ui-editors.md` rows: **`join-request/1`** (`join-request.yaml`): in place, "none (a one-time file the `pair` command writes and the service deletes
  at its next start; the token in it must not pass through a browser; the console's Servers page shows the join)"; **pinned ports**: the console's, Runner's and Workspace's
  `listen:` line (in `settings.yaml` or `server.yaml`, files no entry of `docs/formats.json` covers) is in place, "none (no settings screen; the Servers
  page shows the address)"; the Gateway's (`listen`, `public.listen` and `management.listen` in its `server.yaml`, which no entry of `docs/formats.json` covers): the
  server's settings screen edits them, the Servers page shows the address; **`startup.skip_folder_permissions_check`** (with the flag
  `--skip-folder-permissions-check`): in place, "none (a switch the user sets by hand on purpose; `service check` and the posture page show it)";
  `bundle/1` leaves with INSTALLER-20.
- The console's state: join tokens are the `join_token` table of the `trust` module (kept under the group `console`); the console's Add a server
  screen lists the waiting ones and cancels them.
- `DbDataKit.Cli` (one-word commands: `Noun.Command`), `DbDataKit.State` (`StateLogins.Name`: the console's login names no group).
- `DbDataKit.State.Api` (`GET trust`: every kind with its keys), `DbDataKit.Runner.Api` (its route on the Runner), `DbDataKit.Cli`
  (`RemovedCommands.ConsoleDeploys`, `PublishingGone`); `docs/release-notes-pending.md` (the console's publisher key, the command lines that stopped publishing).
