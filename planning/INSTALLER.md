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
  folder during install (the cause of the workstation failure `not owned by current user`). The Gateway now opens a configuration repository another
  account owns when its own account can write it, allowing only that folder, and otherwise fails naming the folder, its owner and the fix
  (`docs/install.md`, "The Gateway's configuration repository"); the install still makes the folder.
- The four servers' `server run` start through the startup library `Shared/src/DbDataKit.Hosting.Startup`: a failed start writes
  `startup-failure-<program>.txt` (an install's data folder: the shared `logs` folder beside it; INSTALLER-P12 for any other folder) and the Application
  event log for a Windows service, says the step and the file on standard error, and exits non-zero; a port that cannot be bound fails the ports step
  naming the port and Windows' excluded ranges; `service doctor` prints the file. Each server fills in the folder, certificate, state and ports steps
  with the work its start already did (INSTALLER-P13); the keys and join steps are empty. The library: `StartupSequence` (the six steps in order, a failed
  step stops the start and returns exit code 1), `StartupStepException` (what each step's failure names), `StartupPorts` (a refused listener as the ports step's failure), `startup-failure-<program>.txt` (the
  progress line, the full reason, deleted after a clean start; 0600 on Linux, its account, SYSTEM and Administrators on Windows), the service's own
  logs subfolder, `SharedLogsFolder` (the shared folder's protection) and `SuiteServices` (program, Windows name and account, description, data
  folder, start order, and the suite range and port sub-blocks).
- The four servers' `service install` register `DbDataKit`, `DbDataGateway`, `DbDataRunner` and `DbDataWorkspace`, each as `NT SERVICE\<name>`, with
  the description of `SuiteServices` (also in their systemd units); `dist.py` and `msi.py` read the names from the table (`dist/suite_services.py`),
  and the Scoop e2e reads them back with `sc qc` and `sc qdescription` (`docs/install.md`, "Service names, accounts and descriptions").
- Ports: the console, Runner and Workspace take their port from `--listen`, else the `listen:` line of their settings file, else the first free port of their sub-block of 7800 to 7899 (defaults 7800, 7840, 7860), which they write as that line once they listen on it (`ListenPorts` in the startup library; `docs/install.md`, "Ports"); the identity home on its own defaults to 7819; the Gateway keeps 8815 to 8822. `scripts/check_default_ports.py` checks the range, the sub-blocks, the defaults and Windows' default ports.
- Pairing by code, everywhere: `server pair` and `dbdatakit instance add --code` (`ConsoleCli.cs`); `dbdatarunner package pair` and `package publish --code`
  (`RunnerCli.cs`); `dbdatabuild` publish to a Runner `--code` (`CliApp.cs`); `dbdatasync package publish --to runner --code` (`PackageCommand.cs`);
  `dbdatagateway apps publish --to --code` (`AppsCommand.cs`); `dbdataworkspace publisher pair --code` (`WorkspaceAdminCommand.cs`); `gateway add --code`
  of the Runner and of `dbdatastore` (`GatewayNoun` in `DbDataKit.Serving.Notify`, `StoreAdminCommand.cs`). Also announcement (`ConsoleAnnouncer`,
  `--announce-listen`, `DBDATAKIT_ANNOUNCE_URL`) and the bundle token (`bundle/1`).
- The join library in `DbDataKit.Web` (`Shared/src/DbDataKit.Web/README.md`, "Joining the console"): `JoinTokens` (128 random bits, scope, 15
  minutes by default and at most 24 hours, five refused tries; the console keeps a verifier, INSTALLER-P18), `JoinService` (the console's checks, the
  callback, the audit `server.join` with who made the token and never its value), `JoinClient` (the join over TLS pinned to the console's
  thumbprint, signed with a key derived from the token), `JoinEndpoint` (`POST /api/v1/join`), `JoinCallbackEndpoint` and `PendingJoins` (the
  server's answer to the callback), `JoinRequestFile` (`join-request/1`). No host maps the routes or runs the join yet (INSTALLER-12).
- A second Runner instance joins its group by a code made on it (`POST /api/instances/{id}/join`); a Gateway group makes its own key.
- `service check` checks a service's registration, account and folder permissions in every program; `service doctor` answers that it is `service check`
  now (exit 2, the `service` noun's removed verbs); `scripts/check_docs_commands.py` fails on a command or subcommand named `doctor`.
- Enterprise (AD CS) certificates, their renewal and rollover are the subject CERTIFICATES.
- Build's release and the Gateway's Windows build are self-contained; the other programs are framework-dependent.
- The Runner's Windows service job is written and has never run a task under the real service; no Runner release was cut with the release workflow.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| INSTALLER-5 | Reproduce the workstation on CI: a leg of the Windows install test that sets `nodefaultadminowner=1` under `HKLM\SYSTEM\CurrentControlSet\Control\Lsa` (the security option "System objects: Default owner for objects created by members of the Administrators group", so an elevated administrator's objects are owned by the user as on a Windows client, not by the Administrators group as on Server) and turns real-time scanning on (`docs/ci-lessons.md` says how) before installing; run once by hand (`workflow_dispatch`) on the code before INSTALLER-6, required after it | none | S | the dispatched run on the old code shows the Gateway's `not owned by current user` and, without the setting, the same code passes (the run linked in `docs/ci-lessons.md`); if it does not reproduce, the row in `docs/ci-lessons.md` says what differs and the owner's workstation is the check by hand (domain policy and security software are not on a hosted runner) |
| INSTALLER-6 | Services own their data: `service install`, the MSIs and `install.ps1 -InstallService` create only the top folder with its owner and access list (docs/install.md, "A service owns its data"); the services make the `logs` folder on demand ("Logs and the startup-failure file"); keys, certificate, state, configuration repository and `EnsureStarter` happen at first start; Linux: `install.sh --system` makes one system user per service, named as the program (`dbdatagateway`), in the group `dbdatakit`; the units run as those users and use `StateDirectory=` and `LogsDirectory=dbdatakit/<service>`, and the startup library writes the Linux failure file in that folder (`/var/log/dbdatakit/<service>/startup-failure-<service>.txt`; it writes `/var/log/dbdatakit/startup-failure-<service>.txt` today); the Gateway's own git settings file (`<data>/git/.gitconfig`) and its `safe.directory` allow list (docs/install.md, "The Gateway's configuration repository") are removed, since the service owns `config` | INSTALLER-5 | M | the INSTALLER-5 leg passes; after the install nothing exists under the data root but the top folders; after the first start `logs` has its access list (Windows) and `/var/log/dbdatakit/<service>/` is owned by the service's user with mode `0700` and holds the failure file of a failed start (Linux); a test lists every file under each data folder after the first start and finds each owned by the service's account (Windows) or user (Linux, the server started with `sudo -u`); the Linux job runs `install.sh --system` and finds `dbdatakit`, `dbdatagateway`, `dbdatarunner` and `dbdataworkspace` as system users in the group `dbdatakit`, each unit running as its own, and a second run changes nothing; the Gateway's start writes no `git/.gitconfig` and its tests of the allow list are gone |
| INSTALLER-7 | Admin commands through the local API (`DbDataKit.Hosting.Local`) for all four servers: `secret`, `cert`, `state`, `setup link`, `user invite` and the rest call the running service; opening the files is `--recovery`, refused while the service runs, and a file it writes is given the service's account as owner; `WindowsServiceAccounts`' accepted grants (`e0135708`, which taught the command line which service accounts a key file may grant) are then needed only in recovery | INSTALLER-6 | M | with the service running, `secret set` adds the value and no file under the data folder is owned by the administrator; a direct open while it runs is refused naming `--recovery`; recovery with the service stopped writes a file owned by the service's account; Windows leg and Linux job |
| INSTALLER-8 | `--skip-folder-permissions-check` and `startup.skip_folder_permissions_check`: accepted everywhere, a warning at every start, a line in `service check`, the server marked on the console's posture page | — | S | tests: started as a service (detector replaced) and from a terminal it starts with the warning; without it the same folder fails the folder step naming the flag; `service check` and the posture list name the server |
| INSTALLER-10 | The Gateway's listeners (the ones ENDPOINTS-2 leaves) move into 7820 to 7839 (`docs/install.md`, "Ports"): each takes a free value of the sub-block at the first start of its group's first instance and pins it; no listener has a fixed default | ENDPOINTS-2 | S | the Gateway's tests start it with no configured port and find each listener inside its sub-block, pinned after a restart; with 7820 held, the listeners take other free values; `check_default_ports.py` passes with each Gateway listener's entry naming the sub-block `Gateway` (the entries have none while the Gateway keeps 8815 to 8822) |
| INSTALLER-12 | Reversed pairing: the console's `POST /api/v1/join`, `dbdatakit setup invite <gateway\|runner\|workspace> [--role] [--group]`, `pair --host --token --console-thumbprint [--address]` in the Gateway, Runner and Workspace, the join step at start (with `JoinClient`, `JoinCallbackEndpoint` and `JoinRequestFile` of `DbDataKit.Web`), the console keeping the server's pin (`JoinedServer`) and the server keeping the console's (`JoinedConsole`); join tokens as rows of the console's state (an `IJoinTokenStore` over a new table of the `trust` module, with its migration, `scripts/check_state_migrations.py`); `docs/architecture.md` D14 and the Web README say a server calls the console once, to join | none | L | Servers suite (SQLite, PostgreSQL, SQL Server): a console and a real Gateway, Runner and Workspace process each joined by its printed command and listed ready with the address and fingerprint they reported; a reused token is refused; the console's calls succeed pinned; the token table's migration passes `check_state_migrations.py` on the three engines |
| INSTALLER-13 | The console's Add a server screen: product, role ceiling, group, the command to copy, the waiting tokens with Cancel | INSTALLER-12 | S | Playwright story: make a command, run it against a test Runner, the server appears ready; vitest of the form |
| INSTALLER-14 | `dbdatakit setup single-server` and `--stop` (`docs/install.md`, "Setting up one machine"): discovery, registration by library call, the top folder's owner and access list, the program folder's access list (Administrators and SYSTEM full control, everyone else read and execute, inheritance cut, so a user cannot plant a file beside a program a service runs), tokens, start, wait, per-service progress, failure files, the summary (what is ready and what is not, the console's address, the first administrator's setup link from `FIRST-RUN.txt`), safe to run again; `--skip-folder-permissions-check` passed on to the registered services and recorded in their settings (the flag on `server run`, `service install` and `setup single-server`) | INSTALLER-12, INSTALLER-6 | L | Windows CI (zips extracted, no Scoop) and the Linux job (systemd): every service registered, joined and listed; the program folder's access list read back (no write for Users, inheritance off); the summary prints the console's address and the setup link; a second run says nothing to do; a service made to fail (its configured port held) prints its failure file and the summary names it; a run after the fix finishes it; a run with `--skip-folder-permissions-check` leaves the setting in each registered service's settings |
| INSTALLER-15 | Groups through the token: the console makes every group key (a Gateway group's too) and hands it at the join; the join answer carries a link when TRUSTZONES-1 made one; a Gateway joining an existing group gets the group's pinned ports in the join answer and they become its exact configuration (a taken one fails the ports step naming it; another is never picked) | INSTALLER-12, INSTALLER-10 | M | Servers suite: two Gateway instances joined to group X serve each other's sessions with no key copied; the second instance listens on exactly the first's pinned ports; with one of them held on the second's machine, the second fails with a startup-failure file naming the port and listens on nothing; a Runner instance joined to group Y resolves the group's logins; a token for Y used by a Gateway, or for another group, is refused |
| INSTALLER-16 | Server-to-server and publisher trust through the console: a person's publisher client for the command lines (`dbdatarunner package publish`, `dbdatabuild` and `dbdatasync` publish to a Runner, `dbdatagateway apps publish --to`), are registered by the console (INSTALLER-P3); every `--code` of those commands, and `dbdatarunner package pair`, answers the sentence | INSTALLER-12 | M | each command line publishes with a client the console registered; each removed `--code` form and `dbdatarunner package pair` answers its sentence (one test per command) |
| INSTALLER-17 | The Scoop scripts shrink (`docs/install.md`, "Scoop"): no copy; `post_install` prints the next commands; `pre_uninstall` stops the services and on an uninstall prints the `sc.exe delete` commands; verify which `pre_uninstall` runs during `scoop update` (the installed version's) and whether Scoop's running-process check comes before it, with and without `ignore_running_processes`; if both allow it, `pre_uninstall` stops the services on an update too; the outcome in `docs/ci-lessons.md` (its row on "Running process detected, skip updating" rewritten); `dist/README.md` and `docs/suite-single-server-install.md` rewritten | INSTALLER-14 | M | `suite-scoop-e2e.ps1`: install, `dbdatakit setup single-server`, every service ready and registered against `current`; the update check installs a version N whose manifest carries `pre_uninstall`, then updates to N+1 (a new manifest alone cannot show it), and records which `pre_uninstall` ran and whether the running-process check came first, with and without `ignore_running_processes`; the update from N to N+1 by the verified procedure (one command if both allow it, otherwise `setup single-server --stop`, `scoop update`, `setup single-server`) ends with the new version running; uninstall leaves no service running |
| INSTALLER-18 | Docker Compose through the same path: the console's settings list its members (`setup.members`); at its first start it writes one `join-request.yaml` per member into the shared volume `join`; each member's container consumes its own at its start; the quickstart loses `instance pending` and `instance confirm` | INSTALLER-14 | S | `dist/compose/check.sh` on the quickstart: the console and the Gateway are joined with no `instance confirm`; CI runs it |
| INSTALLER-19 | Clean break messages, one per row of the table in `docs/install.md` ("Clean break"): a file in a service's top folder owned by another account (the folder step), a `server.yaml` listing a console paired the old way (a warning at start and in `service check`; the pairing not used), old `instances.yaml` entries (removed by `setup single-server`, marked on the Servers page with the generated command), a `dbdatagateway` registration (stopped, removed, `DbDataGateway` registered on the same folder), services registered from `bin\<version>` (registered against the program folder, the copy removed), `bundle.json` and `--announce-listen` (the console refuses to start), `DBDATAKIT_ANNOUNCE_URL` (a warning at every start), the old default ports (the release notes, `docs/suite-single-server-install.md` and the Servers page), `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` (exit 2, `RemovedCommands`) | INSTALLER-14, INSTALLER-15 | S | one test per row of the table: each finding gives its message (naming what the table's last column says), exit code or warning, from the one the table names as saying it; the old-port row is checked by `check_docs_commands.py` and a test that the Servers page shows the reported address |
| INSTALLER-20 | Delete the old pairing and install code: `PairingCodes`, `pairing-codes.json`, the code verbs (`server pair`, `dbdatarunner package pair`, `instance add --code`), `PairingClient`'s code exchange, the announcement, the bundle (`bundle/1` leaves `docs/formats.json` and `docs/ui-editors.md`), the group join route, `publisher pair --code`. Voids: the Gateway pairing of the Store command line (`dbdatastore gateway add`, with PUBLISHING-2) | INSTALLER-13 to INSTALLER-19 | M | the suites pass with those types gone; the removed verbs answer their sentence; `check_formats.py` and `check_ui_editors.py` pass without `bundle` |
| INSTALLER-21 | A service writes its own `state:` settings: at first start from the settings `setup single-server` or the MSI pass (`STATE_ENGINE`, `STATE_CONNECTION` with a secret reference), never a password; one login per service and group | INSTALLER-14, TRUSTZONES-2 | M | Windows leg: an MSI install with the two properties starts the service on SQL Server with the login it names; Linux: `install.sh` the same on PostgreSQL; a password in the property is refused |
| INSTALLER-22 | The Runner's release and Windows service legs: a release cut with `dbdatarunner-release.yml`, the MSI installed on Windows, a task run under `NT SERVICE\DbDataRunner`; a package's manifest generated by a build step (`ManifestGenerator.ForAssembly`) | INSTALLER-6 | M | the `windows` job of `dbdatarunner-ci.yml` installs the service, runs a task, reads its run and removes the service; a snapshot release publishes the Runner's zips and MSI |
| INSTALLER-23 | Framework-dependent Build and Gateway: `scripts/publish.sh`, `build/publish.ps1`, `package.ps1`, `requiresDotnet` in `dist.json`; the 32-bit ODBC worker stays self-contained | none | M | `dist/tests` and the suite dry run pass with every zip framework-dependent but the worker; `dbdatabuild version` and a Gateway query run on a machine with only the .NET 10 runtime; the zip sizes are in the commit |
| INSTALLER-25 | First-run remainders: `DbDataKit/docs/scale-out.md` rewritten for joins by token (state move, another Gateway instance, a server on another machine); the Gateway and the Runner still install, start and join alone from their own MSI | INSTALLER-14 | S | `ConsoleScaleOutTests` follow the document's commands; the per-product MSI jobs (`dist-msi-ci.yml`) pass with a join by token |
| INSTALLER-26 | Container images for the Runner and the Workspace, so the Compose quickstart runs all four services (queued: wanted, not yet) | INSTALLER-18 | M | `dist/compose/check.sh` runs four containers joined, and a Build refresh task runs on the Runner's container |
| INSTALLER-27 | Console-started pairing for networks where a server cannot reach the console (low priority): `setup invite` and the Add a server screen offer it; the console still generates the command for the remote, code included; it then either takes the remote's address up front, or waits for the user to run the command there and enter the address the command prints; it then connects to the server and pins it, with the token's scope and single use | INSTALLER-12, INSTALLER-13 | M | Servers suite: a Runner whose calls to the console are blocked joins by this path and is listed ready, once with its address given up front and once with the address the command printed entered afterwards; a reused or expired code is refused; a Playwright story of entering the address |
| INSTALLER-28 | Serving groups upgrade rolling, draining first (waits for the first stable release) | TASKENGINE-9 | L | the servers suite upgrades a two-instance group with a query running; no query fails; the second instance waits for the first |
| INSTALLER-29 | The compatibility job runs the previous release's programs against the current build (waits for the first stable release); the release workflows write `release.json` with the compatibility generation, so the console's "outside N-1" mark shows for a release from GitHub, not only from a mirror | none | M | the `compatibility` job pairs the previous stable console with the current Gateway and Runner and the reverse, and a publish and a query pass; a snapshot release carries `release.json` with its generation and the console's Upgrades page marks a release outside N-1 from it |

## Needs from other subjects

- ENDPOINTS-2: the Gateway's listeners and their default ports, before INSTALLER-10 pins them.
- TRUSTZONES-2: a state login per service and group, before INSTALLER-21 writes it.
- TRUSTZONES-1: the link the join answer carries (INSTALLER-15 carries it when it exists).
- TASKENGINE-9: two Gateway instances behind a balancer in CI, before INSTALLER-28.
- PUBLISHING-2: the Store command line's `gateway add` goes with `dbdatastore model publish --proxy`.

## Proposed decisions

### INSTALLER-P3: how a person's command line publishes without a code (awaiting the owner)

Today `dbdatarunner package publish --code` pairs the person's machine with a Runner. With every code gone: option A, `dbdatakit setup invite publisher
--target <runner|gateway> --role publisher` prints `<program> client join --host ... --token ...`, which makes a client key on the person's machine and the
console registers it with the target; option B, command lines stop publishing and only the control service publishes (the Workspace service never publishes: WORKSPACEISOLATION). Scenario: a developer runs
`dbdatabuild project publish --to runner` from a laptop; under A it works after one `client join`, under B it answers "publish from the console".
Recommendation: A, so the command lines stay usable without the Workspace.

### INSTALLER-P4: where the check for commands named `doctor` lives (default, awaiting the owner)

The check is a fourth check of `scripts/check_docs_commands.py` (`check_names`, with the list `NAMES_NOT_USED`), which CI already runs in `repo-checks.yml`
and which already reads every command line's nouns and verbs from the sources. Scenario: someone adds `Forward("doctor", ...)` to a noun; the
`check-docs-commands` job fails naming `dbdatakit service doctor` and saying to call it `check`. Option A (built): keep it there. Option B: a separate
script `scripts/check_command_names.py`, which needs a new step in `repo-checks.yml` and a new entry in the list of checks in `CLAUDE.md` (a file only
the owner changes). Recommendation: A; one reader of the command trees, and nothing changes in `CLAUDE.md`.

### INSTALLER-P5: the library types behind `service check` keep the name `Doctor` (default, awaiting the owner)

The command, its usage, its help, its last line (`Check: OK (n warning(s)).`) and `ServiceCommandContext.ExtraChecks` (was `ExtraDoctorChecks`) say
`check`; the types in `DbDataKit.Hosting.WindowsService` are still `ServiceDoctor`, `DoctorCheck`, `DoctorReport` and `DoctorSeverity`, and the
`DoctorTests` class keeps its name. Scenario: a developer reads `ServiceCommand.Check` and finds it calls `new ServiceDoctor(...)`. Option A (built):
leave the types; the repository check covers command names only. Option B: rename them to `ServiceCheck`, `ServiceCheckItem`, `ServiceCheckReport`
and `CheckSeverity` in a follow-up commit (no behaviour change, about ten files). Recommendation: B, so the code says what the command says.

### INSTALLER-P6: the two lessons in `docs/ci-lessons.md` that name `service doctor` (default, awaiting the owner)

Two rows of `docs/ci-lessons.md` describe failures seen as `service doctor`. The rule for shared files is to add entries, never to rewrite others', so
the row that types `dbdatarunner service doctor` got the marker `retired-name-ok` (otherwise `check_docs_commands.py` fails on it as an unknown command)
and the text is unchanged. Scenario: a reader searches the lessons for `service check` after a failure of that command and does not find these two
rows. Option A (built): keep the rows as they are, marked. Option B: change `service doctor` to `service check` in both rows (the lessons still apply
word for word to the renamed command). Recommendation: B, with the owner's word, since it edits entries someone else wrote.

### INSTALLER-P9: a failure file or logs subfolder another account owns (default, awaiting the owner)

`docs/install.md` says a service that finds its own subfolder or failure file owned by another account writes nothing there and says so in the
event log. Scenario: an administrator ran `dbdatagateway server run` elevated once, so `startup-failure-dbdatagateway.txt` is owned by that
administrator; the service then starts as `NT SERVICE\DbDataGateway`. Built as the default: the start goes on, nothing is written to the file, and each
reason goes to standard error and, for a Windows service, the Application event log (source `.NET Runtime`, event 1000; on Linux standard error is
the journal). The other option is to fail the start at once, naming the file and its owner. Recommendation: the default, because the file only
reports a start and a stale file should not stop a service; `service doctor` (`service check` after INSTALLER-1) prints the file when it can read
it, and could also name its owner, which is not built.

### INSTALLER-P10: what the startup library fixes that the doc does not name (default, awaiting the owner)

Scenario: `setup single-server` starts the services of one machine, and a script checks a failed start's exit code. Built as defaults: the start
order in `SuiteServices` is console, Gateway, Runner, Workspace (the console first, since the others join it; the Workspace last, since it
publishes to the Runner and the Gateway); a failed start exits with code 1; `SuiteServices` also holds the suite range and the port
sub-blocks. Options for the exit code: 1 (built), or one code per step so a script can tell the steps apart without reading the file.
Recommendation: keep 1 and the order; the file names the step.

### INSTALLER-P11: how Windows CI shows a second service account cannot open the file (default, awaiting the owner)

The task's check is that on Windows a second service account cannot open the first's failure file. A virtual service account (`NT SERVICE\X`)
exists only for a registered service and a test cannot log on as one. Built as the default: one test reads the file's access list (inheritance cut,
grants only to its account, SYSTEM and Administrators), and a second makes an ordinary local user with `net user`, logs on as it
(`LogonUser`, network logon) and fails to open the file; the second skips, saying why, when the runner cannot make or log on that user. The other
option is to register a throwaway service and run a helper program as it. Recommendation: the default; the helper service is slower and adds an
install step to a library test.

### INSTALLER-P12: where the failure file is for a server whose data folder is not an install's (default, awaiting the owner)

`docs/install.md` names the install's paths only. Scenario: a developer runs `dbdatarunner server run --repo ~/work/runner-a` on Linux without root,
and a test runs `server run --repo /tmp/dbdr-start-x1`; neither can or should write to `/var/log/dbdatakit` or `C:\ProgramData\DbDataKit\logs`. Built
as the default (`SuiteServices.LogsRootFor`), from the data folder the server runs on:

- the install's data folder (`C:\ProgramData\DbDataKit\runner`, `/var/lib/dbdatakit/runner`): the machine's logs folder (`C:\ProgramData\DbDataKit\logs`,
  `/var/log/dbdatakit`);
- a folder named for its service under another root (`D:\Data\runner`, as `DBDATAKIT_DATA=D:\Data` makes it): `logs` beside it (`D:\Data\logs`);
- any other folder (`~/work/runner-a`): `logs` inside it (`~/work/runner-a/logs/startup-failure-dbdatarunner.txt`).

`service doctor` finds the file by the same rule. The other options: B, always the machine's folder (a run without root on Linux then writes no file
and says why on standard error, and every test run on Windows writes into the machine's folder); C, a variable naming the logs folder
(`DBDATAKIT_LOGS`), with B when it is not set. Recommendation: A, so a run from a terminal and a test keep their logs with their data and an install
gets the documented paths; `docs/install.md` then says it in "Logs and the startup-failure file".

### INSTALLER-P13: what each server's steps hold until its start is split further (default, awaiting the owner)

The servers' starts were not written as six steps. Scenario: the Runner starts on a machine where its task host is missing, and an administrator reads
`startup-failure-dbdatarunner.txt`. Built as the default:

- folder: the server reads its settings and makes its folder; the Gateway reads and validates `server.yaml` and builds the server. A settings error
  keeps its exit code (Gateway 2, Runner 2, console and Workspace 10); every other failed start exits 1. The Gateway also deletes a `start-failure.txt`
  an earlier release left in its data folder.
- certificate: the console, Runner and Workspace load or make `tls.pfx`. The Gateway loads its certificate when its listeners are configured, in the
  ports step.
- state: the Runner opens its state and builds the server; the Workspace builds the server, which opens its state; the console builds the server and
  opens its state.
- ports: the host's start, which binds the listeners and also starts the background services (the Runner's task host check, the Gateway's state
  migrations and modules). A failure of one of those is written at the ports step, with its own message and "What to check: The server's services
  start together with its listeners, and one of them failed: the message above says which and why."
- keys and join: not filled in (INSTALLER-6 makes the keys at first start; INSTALLER-12 adds the join).

In the scenario the file says "failed at the ports step" with the task host's message. Options: A, as built; B, a seventh step for the background
services in `docs/install.md`, between state and ports; C, each host starts its background services in the state step and binds its listeners in the
ports step (a hook that runs just before the listeners bind). Recommendation: A now, and C with INSTALLER-6, which moves keys, certificate and state
to the first start and so rewrites each server's start.

### INSTALLER-P14: what `service doctor` does with the failure file (default, awaiting the owner)

Scenario: the Gateway's service failed to start, and an administrator runs `dbdatagateway service doctor` (`service check` after INSTALLER-1). Built
as the default: after the registration and folder checks it prints `Startup-failure file: <path> (the last start failed):` and the file's lines
indented, or `Startup-failure file: none (<path>)`, or that the file is there and this account cannot read it; the data folder is the registration's
(or `--data-dir`). The exit code stays the checks' verdict: a failure file alone does not make it 1. The other option: a failure file that holds a
failed start is a failed check (exit 1). Recommendation: the default, because the checks look at the registration and the folders and the service
manager already reports a failed start; INSTALLER-1 renames the verb and the printing moves with it.

### INSTALLER-P15: the display name and the systemd description of each service (default, awaiting the owner)

`docs/install.md` gives each Windows service its name, account and description, and no display name. Scenario: an administrator opens the Services
console (`services.msc`) or runs `systemctl status dbdatarunner`. Built as the default: the display name is the service's name (`DbDataRunner`; it was
`DbDataRunner server`, the Gateway's `DbDataGateway Arrow Flight SQL gateway`), and a systemd unit's `Description=` is the table's description (it was
`DbDataRunner server`, `dbdatakit console`). Options for the display name: A, the name (built); B, a short phrase per service in the table
(`DbDataKit runner`), a new column. Options for systemd: A, the description (built); B, the display name. Recommendation: A and A, so the one table
gives every service manager the same words; `docs/install.md`, "Service names, accounts and descriptions", then says so, and the release note gains a line.

### INSTALLER-P16: a Gateway registered by an earlier release as `dbdatagateway` (default, awaiting the owner)

Windows compares service names without case and makes `NT SERVICE\<name>`'s SID from the upper-case name, so the old registration is the same service
with the same account. Scenario: a machine upgraded with the Gateway's MSI keeps its service as `dbdatagateway`; `Get-Service` shows that spelling
and `sc qc` the old account string and description. Built as the default: `dbdatagateway service install` updates that service in place (description,
display name) and keeps the stored spelling; no start or `service check` message is given, since the service works; `setup single-server` re-registers
it under the new spelling (`docs/install.md`, "Clean break", INSTALLER-19); `scoop update` of `dbdatakit-full` already does, since it registers each
service again from the new version. Option B: `service install` deletes and creates the service when the stored spelling differs, so the spelling is
exact after any install. Recommendation: the default; a delete and create stops a running service for a change of letter case.

### INSTALLER-P17: how `dist.json` and the Scoop binding read the table (default, awaiting the owner)

The table is C# and the installers are bound by Python. Scenario: someone renames a service in `SuiteServices.cs` and binds a release. Built as the
default: `dist/suite_services.py` reads the four entries from `SuiteServices.cs` (and fails naming the file when it reads fewer than are declared);
`dist.py` and `msi.py` take a suite product's service name from it, a suite product's `dist.json` has no `service.name`, and one that names another
is refused naming both; the Scoop e2e reads the names, accounts and descriptions it checks from the same script. Option B: `dist.json` keeps
`service.name`, and a test compares it with the table. Recommendation: the default, so a rename is one edit in one file.

### INSTALLER-P18: what the console keeps of a join token (default, awaiting the owner)

`docs/install.md` ("Pairing") says the token table holds the token's hash. Scenario: someone copies the console's state database (a backup, a shared
PostgreSQL) while a token for a Runner with the admin ceiling is waiting. Built as the default: the console keeps a verifier, the public half of an
Ed25519 key derived from the token (HKDF-SHA256), and the join carries a signature made with that key; the copy cannot make a signature, so it cannot
join a server. Option B: keep a SHA-256 hash and prove with an HMAC keyed by it, as pairing codes do; a copy of the hash then joins a server as admin
until the token expires. Recommendation: the default; on that answer `docs/install.md` says "a verifier made from the token" where it says "the
token's hash".

### INSTALLER-P19: what counts as one of a token's five tries, and how long ended tokens are kept (default, awaiting the owner)

Scenario: a Runner's join fails because the console cannot reach the address it gave; the administrator fixes the firewall and restarts the service
four times. Built as the default: a refused try is counted against the token when the request names a token the console made and is refused for a
signature that does not match, another product, another group or a failed callback; the fifth cancels the token. Not counted: a token the console
never made (audited as made by `unknown`), a spent, expired or cancelled token, a request that is not a join, and a server more than one
compatibility generation away. Ended tokens stay in the store, so a late join with a spent or expired token is audited with who made it, until the
store removes them (`IJoinTokenStore.RemoveEndedBefore`; when it runs is INSTALLER-12's). Option B: five refused joins of any kind in a row cancel
every waiting token, as pairing codes do; anyone who can reach the console could then cancel every waiting token. Recommendation: the default.

### INSTALLER-P20: which group a joining server may name (default, awaiting the owner)

Scenario: a Gateway already in group `ops` (its settings say so) runs a command made for group `sales`. Built as the default: the join request names
the server's group when it has one; the console refuses it unless the token names the same group; a server with no group takes the token's group,
or none when the token names none. So a token that names no group refuses a server that is already in a group. Option B: a token without a group
admits a server of any group. Recommendation: the default, so a server never changes group, or keeps one the console did not intend, through a
token that did not say so.

### INSTALLER-P21: what a refused server is told (default, awaiting the owner)

Scenario: a Runner's join fails and the administrator reads its startup-failure file. Built as the default: when the request's signature proves the
server holds the token, the console says why ("The join token was already used", "expired", "was made for a gateway", "is in group ops", "could not
confirm this server at https://...: it presented the certificate sha256:..."), each with what to do; otherwise it says only "The console did not
accept the join. Make a new command on the console (dbdatakit setup invite) and run it on this machine." A server more than one compatibility
generation away is told both versions and the side to upgrade (`409`, before the token is looked at). The console's audit and log always have the
full reason. Option B: one sentence for every refusal, as pairing does. Recommendation: the default; only the token's holder learns anything.

### INSTALLER-P22: a wrong console thumbprint is refused on the joining server only (default, awaiting the owner)

The join library's "Done when" asked that a wrong thumbprint be refused and audited with who made the token. Scenario: the command was copied with
another console's thumbprint, or something answers at the console's address with its own certificate. Built as the default: the joining server's TLS
handshake fails before anything is sent; `JoinFailedException` names the address, the certificate presented and the thumbprint expected (the join
step writes it to the startup-failure file); the console receives no request, so it audits nothing and the token stays waiting. Option B: none
that keeps the token unsent; a console cannot audit a connection that never reached it. Recommendation: the default, and the "Done when" read
as built.

### INSTALLER-P23: how a join token is written (default, awaiting the owner)

Scenario: an administrator copies `dbdatarunner pair --host https://console:7800 --token k7d2mq4x5vbn6r3ta2wq4zj7pa --console-thumbprint sha256:...`
from the console. Built as the default: 16 random bytes in the lower-case RFC 4648 base32 alphabet, 26 characters with no prefix; upper case,
dashes and spaces are accepted, so the console may show it in groups. Option B: a prefix such as `dbdj_`, so secret scanners and people recognise a
leaked token; option C: the pairing code's alphabet in groups of five. Recommendation: the default, with B worth adding if the owner wants scanners
to find tokens in logs and chat.

### INSTALLER-P24: how the console's callback is confirmed (default, awaiting the owner)

`docs/install.md` says the console calls back the joining server with its fingerprint pinned. Scenario: someone with a token gives the address and
fingerprint of another, already running server, so the console would pin a server that never asked to join. Built as the default: the callback is
`POST /management/v1/join/callback` with the join's random id, and only a server waiting on that id answers, signing the id, the console, the address
and the certificate with the server's own Ed25519 key, whose public half the signed join carried. So each server needs a key of its own when it joins
(where it keeps it is INSTALLER-12's), and its HTTPS listener must answer the callback during the join step. Option B: the server signs the callback's
answer with the token's key instead, so no key of its own is needed. Recommendation: the default; the console then holds a key that identifies the
server, which a later re-join or a server's own calls can check.

### INSTALLER-P25: where a server keeps the port it chose (default, awaiting the owner)

Scenario: a Runner starts for the first time with no `--listen`, finds 7840 held and listens on 7841; at its next start it must listen on 7841 again.
Built as the default: once the server listens, it writes a top-level line `listen: https://127.0.0.1:7841` (the whole address, under a comment) to
its own settings file, `server.yaml` for the Runner and the Workspace and `settings.yaml` for the console (made if it is not there, which the console
did not do before); a person can edit the line to move the server, and `--listen` overrides it without writing anything. Option B: a file of the
server's own (`ports.yaml`) that people do not edit; option C: a `ports:` section holding the port only, the host staying the default.
Recommendation: the default, because one line in the file the person already edits says where the server listens and how to move it.

### INSTALLER-P26: a pinned port another program holds at a later start (default, awaiting the owner)

Scenario: the Runner pinned 7841; after a reboot another program holds 7841. Built as the default: the pinned port is treated like a configured one,
so the ports step fails and the failure file names 7841 (and the excluded ranges); the Runner does not move. Option B: take the next free port of the
sub-block and pin that one, so the service starts but the console and the publishers paired on 7841 no longer reach it. Recommendation: the default,
the same rule as the Gateway's group ports in `docs/install.md`: a client set once keeps working, or the start says why not.

### INSTALLER-P27: the identity home on its own keeps a fixed port (default, awaiting the owner)

Scenario: `dbdatakit identity run` on the console's machine with no `--listen`. Built as the default: it listens on 7819, the last port of the
console's sub-block, as a fixed default (never chosen or pinned), so a console that took the next free port from 7800 does not meet it. Option B:
7801; option C: choose and pin a port of the sub-block as the console does. Recommendation: the default; an identity home is set in the consoles that
use it, so its port should not depend on what else was running at its first start.

### INSTALLER-P28: the container images and the compose files keep their ports (default, awaiting the owner)

Scenario: `docker compose -f dist/compose/quickstart.yml up` after this release. Built as the default: the console's image still runs
`--listen https://0.0.0.0:8480` and the compose files publish 8480 and 8815, because a configured port is never moved and the Gateway's ports have
not moved yet; `docs/quickstart.md` is unchanged. Option B: move the image and the compose files to 7800 now. Recommendation: move the image, the
compose files and the quickstart together with the Gateway's listeners (INSTALLER-10), so a compose user changes their ports once.

### INSTALLER-P29: `health check` asks the default address (default, awaiting the owner)

Scenario: the Runner pinned 7841 and an administrator runs `dbdatarunner health check`. Built as the default: it asks `https://localhost:7840`, the
default, and fails; `--url https://localhost:7841` asks the right one. `setup link` and `user invite` already start their links with the console's
`listen:` address. Option B: `health check` reads the `listen:` line of the server's settings file (`--repo`, or the default folder) first.
Recommendation: B, a small change of the shared `health` noun; until then the release notes say to give `--url`.

### INSTALLER-P30: the suite install pairs the members on their default addresses (default, awaiting the owner)

Scenario: `scoop install -g dbdatakit-full` on a machine where 7840 is held; the Runner takes 7841 at its first start. Built as the default: the
install still pairs each member on its default address (`https://127.0.0.1:7840` for the Runner), so that pairing fails naming the address, and the
notes say to pair by the server's own address. Option B: the install reads each member's `listen:` line after its first start and pairs on it.
Recommendation: the default until the join (INSTALLER-12), where each server reports its own address and the install stops guessing.

### INSTALLER-P31: the Gateway's listeners in the port check (default, awaiting the owner)

Scenario: `scripts/check_default_ports.py` checks that every default lies in its service's sub-block. Built as the default: the Gateway's eight
listeners are listed with no sub-block, so they are checked only for a shared default and for Windows' default ports while they keep 8815 to 8822;
INSTALLER-10 gives them the sub-block `Gateway` when they move. Option B: fail on them now, which fails the repository until INSTALLER-10 lands.
Recommendation: the default.

## Shared areas touched

- New `Shared/src/DbDataKit.Hosting.Startup`; `DbDataKit.Web` (pairing, join), `DbDataKit.Hosting.Cli` (`ServiceCommand`, `HostingNouns`),
  `DbDataKit.Hosting.WindowsService`, `DbDataKit.Hosting.Local`, `DbDataKit.Hosting.Certificates.Windows`, `DbDataKit.Serving.Notify` (pairing verbs).
- `dist/` (suite scripts, `dist.json` files, compose), `scripts/check_default_ports.py`.
- `docs/formats.json` (`join-request/1` added, the entry is in place; `bundle/1` removed), `docs/architecture.md` (D14), `docs/ci-lessons.md`,
  `docs/release-notes-pending.md`, `DbDataKit.slnx` (the new project).
- `docs/ui-editors.md` rows: **`join-request/1`** (`join-request.yaml`): in place, "none (a one-time file the `pair` command writes and the service deletes
  at its next start; the token in it must not pass through a browser; the console's Servers page shows the join)"; **pinned ports**: the console's, Runner's and Workspace's
  `listen:` line (in `settings.yaml` or `server.yaml`, files no entry of `docs/formats.json` covers) is in place, "none (no settings screen; the Servers
  page shows the address)"; the Gateway's (INSTALLER-10, its `server.yaml` keys; if a key is added, the format's entry is bumped in the same task): the
  server's settings screen edits them, the Servers page shows the address; **`startup.skip_folder_permissions_check`** (INSTALLER-8): "none (a switch the user sets by hand on purpose;
  `service check` and the posture page show it)"; `bundle/1` leaves with INSTALLER-20.
- The console's state: join tokens are a new table of the `trust` module with its migration (`scripts/check_state_migrations.py`, INSTALLER-12); the
  Servers page lists the waiting ones and cancels them (INSTALLER-13).
