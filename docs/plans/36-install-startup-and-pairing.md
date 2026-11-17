# Plan 36: Install, startup and pairing

Status: **proposed** (the owner's direction and decisions of 2026-11-17, and the same day's answers to its questions, now decisions 13 to 18; nothing built). Slice ids `IS01`... (`IS` followed by a number is free in every plan).
It comes before further work on the secrets review and on [plan 35](35-isolation-by-trust.md) I4 to I9 (decision 12). It replaces [plan 28c](28c-engines-on-runner.md)
E20d's join by a code and the rows E20e and E20f (decision 10). Same rules as the other slice plans (`CLAUDE.md`, "Making changes"): one slice, one commit whose
message starts with its id, "done" is a test, the status goes in the slice's row in the same commit.

## The problem

On the owner's Windows 11 domain-joined workstation the suite's snapshot install kept failing in ways CI did not show. CI runs on Windows Server runners. Each
failure had one cause: **a file was created by one account and used by another.** The elevated installer created the Gateway's data (its state, its keys, the
`config` folder of its configuration repository) before the service had ever run, as the administrator, and the service then used it as `NT SERVICE\<name>`:

- elevated admin commands crashed on key files that grant the service's account (`KeyPermissionException: unexpected grant to NT SERVICE\DbDataKit`; fixed in
  `e0135708` by widening which grants the command line accepts, `WindowsServiceAccounts`);
- the Gateway's service did not start: `LibGit2SharpException: repository path 'C:/ProgramData/DbDataKit/gateway/config' is not owned by current user`. The
  folder was made by `dbdatagateway service install` (`GatewayConfigFile.EnsureStarter` writes `config\server.yaml`) running elevated. On a Windows **client** an
  object an elevated administrator creates is owned by that user; on Windows **Server** it is owned by the Administrators group (the security option "System
  objects: Default owner for objects created by members of the Administrators group", registry value `nodefaultadminowner` under
  `HKLM\SYSTEM\CurrentControlSet\Control\Lsa`). That is the likely reason CI passed; IS04 verifies it;
- when the Gateway did not start, nothing said why: a configuration error went to standard error, which a service does not have (`80655d9b` added a Gateway-only
  `start-failure.txt`).

Each fix so far patched one symptom in one product. This plan makes install, startup and pairing one consistent system, so this class of bug stops: a service
creates and owns all of its own data, every service starts through the same sequence and reports a failed start in the same place, and the console is the only
place pairing starts from.

## What exists (read in the code, 2026-11-17)

- **The single-server package** `dbdatakit-full` (`dist/suite-scripts.ps1`, bound by `dist/dist.py bind --suite` from `dist/suite.json`; `dist/README.md`,
  "One package for a single server"; plan 33 SC12, SC13). `post_install` copies the program files to `%ProgramData%\DbDataKit\bin\<version>` (Scoop refuses to
  update an app while a process runs from its folder, `docs/ci-lessons.md`), runs each member's `service install` from the copy, starts the console, then for each
  other member runs `<program> server pair --max-role <roles>`, reads the code from its output and runs `dbdatakit instance add <address> --code <code>`, and
  prints the setup link from `FIRST-RUN.txt`. `pre_uninstall` unpairs, stops and unregisters on an uninstall, and only stops on an update, telling the two apart by
  Scoop's `$cmd` or by the folders.
- **Registration** is the shared `ServiceCommand` (`DbDataKit.Hosting.Cli`) over `ServiceProvisioner` and `DirectoryLayout` (`DbDataKit.Hosting.WindowsService`):
  each product passes its `ServiceCommandContext` (name, description, layout with access lists). The access lists are applied by the elevated command, so the
  folders it creates are owned by the administrator (a user on a client, the Administrators group on Server). The Gateway's install also writes its starter
  settings (`EnsureStarter`).
- **Names and accounts:** the console `DbDataKit` (`ConsoleService`), the Runner `DbDataRunner`, the Workspace `DbDataWorkspace`, each `NT SERVICE\<name>`; the
  Gateway is registered as **`dbdatagateway`** (`DbDataGateway/dist.json`, `--name {name}`; the default in `ServiceCli` is lower-cased), so its account shows as
  `NT SERVICE\dbdatagateway`. The account's SID is computed from the upper-cased name (`WindowsPrincipalResolver`, `WindowsServiceAccounts`), so both spellings
  are the same SID; the difference is in what people read. The descriptions are long and uneven ("Arrow Flight SQL gateway that forwards queries to ODBC/ADO.NET
  databases and compresses the results.").
- **Logs:** each layout has `<data>\logs`; the Gateway writes `start-failure.txt` in its data folder (`StartFailure.cs`, `80655d9b`); the others write nothing
  when a start fails before logging is up.
- **Admin commands open the service's files directly** (key rings, `server.yaml`, `pairing-codes.json`, the state), elevated, which is why `e0135708` had to
  teach the command line which service accounts a key file may grant. The Gateway has a local channel (`DbDataKit.Hosting.Local`, `management.localChannel`, a
  named pipe or Unix socket); the console, the Runner and the Workspace have none that their commands use.
- **Ports** are fixed defaults (console 8480, identity home on its own 8482, Workspace 8471, Runner 8495, Gateway 8815 to 8822), checked unique by
  `scripts/check_default_ports.py`. A port Windows excludes (Hyper-V and WinNAT reserve ranges, `netsh int ipv4 show excludedportrange protocol=tcp`) fails the start.
- **Pairing, three ways today** (`Shared/src/DbDataKit.Web/README.md`):
  1. **A code made on the server:** `server pair` (`PairingVerbs`, `PairingCodes`: 20 characters, the hash in `pairing-codes.json`, 10 minutes, five wrong tries
     cancel them), redeemed by the console (`dbdatakit instance add --code`, `PairingClient`; the code never crosses the wire, both ends prove they hold it). The
     console then calls the server; a server never calls a console (architecture D14).
  2. **Announcement:** the console's `--announce-listen` (plain HTTP, 8481), a server's `DBDATAKIT_ANNOUNCE_URL` (MSI `ANNOUNCEURL`, `install.ps1 -AnnounceUrl`,
     `install.sh --announce-url`), `ConsoleAnnouncer`, then `dbdatakit instance pending` and `instance confirm`.
  3. **The single-machine bundle** (plan 23a FQ18): `bundle.json` (`bundle/1`) and a token file only the service accounts read; a server on loopback that
     presents it is paired with no one confirming.
  - **Groups:** pairing a Runner joins it to its group and the console hands it the group key; another instance joins with a code made on it
    (`POST /api/instances/{id}/join`, `ConsoleGroupKeys`, plan 28c E20d). A Gateway group makes its own key on its first instance and an operator copies it.
  - **Server to server:** the Workspace pairs its publisher keys with a Gateway or a Runner by a code made there (`dbdataworkspace publisher pair --code`).

## Decisions (owner, 2026-11-17)

| # | Decision | Why (the problem it removes) |
| --- | --- | --- |
| 1 | **Scoop install is simple.** No program files are copied (unchanged): the services are registered against Scoop's `current` folder, which Scoop repoints on update. `post_install` only prints the next commands (`dbdatakit setup single-server`, and how to update); no other service command is run by the Scoop scripts. **Updating:** the user stops the services first, with our command `dbdatakit setup single-server --stop` or a system command (`Stop-Service`, `sc.exe stop`), then runs `scoop update`, then `dbdatakit setup single-server`. On an uninstall `pre_uninstall` stops the services and prints the `sc.exe delete` commands. IS15 verifies what Scoop runs during an update and, if it holds, lets `pre_uninstall` stop the services itself so an update can be one command ("Scoop update" below). The single-server package's scripts shrink to that. | The scripts grew to 200 lines of PowerShell that copy, register, pair, scrub codes and guess whether an update is an uninstall; each workstation difference broke a different line. What runs as an elevated script in someone else's process cannot be tested like our own code. |
| 2 | **A service creates and owns all of its own data.** The install only registers the service and creates its top folder with its access list. Everything inside (subfolders, keys, certificate, state, the configuration repository, logs) is created at first start by the service's own account. An admin command that needs a service's data goes through the service's local API; opening the files directly is an explicit recovery mode, only while the service is stopped. | The two workstation failures above: a file made by the administrator and used by the service (the git owner check), and a file made by the service and opened by the administrator (the key file's grants). If only one account ever creates the files, the owner and the grants are the same on a client and on Server. |
| 3 | **One startup sequence for every service**, in a shared library (`Shared/src/DbDataKit.*`): settle the folder and its permissions, then keys, then the certificate, then the state and its migrations, then bind the ports, then join the console. The same on Windows and Linux. | Each server starts in its own order today, so a failure in one step looks different in each product, and a fix (the Gateway's start-failure file) reaches one product only. |
| 4 | **Windows service names are exactly `DbDataKit`, `DbDataGateway`, `DbDataRunner`, `DbDataWorkspace`**, each running as `NT SERVICE\<that name>` (the Gateway's is lower case today: fixed). The descriptions are exactly: `DbDataKit` "DbDataKit console: sign-in, users and management of the Gateway, Runner and Workspace servers."; `DbDataGateway` "DbDataKit gateway: serves data, semantic models, master data, apps, dashboards and reports to people, applications and agents."; `DbDataRunner` "DbDataKit runner: runs published tasks (data loads, replication, deployments and quality checks) on schedule or on demand."; `DbDataWorkspace` "DbDataKit workspace: where people and agents author, test and publish apps, data models, replications and master data." | An administrator reading `services.msc` or an access list sees `dbdatagateway` beside `DbDataKit` and a description that names ODBC and compression; names that differ by product invite scripts that guess. |
| 5 | **Logs in one place:** `C:\ProgramData\DbDataKit\logs\<service>\` (Linux `/var/log/dbdatakit/<service>/`). **A failed start is reported in a shared, known file** `C:\ProgramData\DbDataKit\logs\startup-failure-<service>.txt`: at each start the service replaces it with a line "starting since <time>, not finished"; it deletes it when startup succeeds; when startup fails it writes the full reason (the account, the folders, the exception chain, hints such as Windows' excluded port ranges). So no file means a clean start, and a leftover "not finished" line means a crash the service could not report. The install creates `logs`; each service account may create, and owns, only its own subfolder and its own failure file; no service reads another's. This replaces the Gateway-only `start-failure.txt` (`80655d9b`). | "Cannot start service dbdatagateway" with nothing else: the reason was on a standard error nobody reads, in a data folder each product puts elsewhere, or nowhere. One path the installer and `setup` can read means the reason is printed where the person is. |
| 6 | **Ports: one suite range, 7800 to 7899**, outside Windows' defaults (80 and 443, 135 and 445, 3389, 5040, 5357, 5985 and 5986, 7680, 47001, the dynamic range 49152 to 65535) and the common developer ports. Each service has a sub-block and a default: the console 7800 (7800 to 7819), the Gateway 7820 to 7839 (its listeners take any free values of its sub-block, with no fixed split between them), the Runner 7840 (7840 to 7859), the Workspace 7860 (7860 to 7879); 7880 to 7899 are spare. When none is configured, a service takes a free port of its sub-block at first start and pins it in its own settings. Windows' excluded port ranges are still skipped (a machine whose dynamic range was lowered can reserve inside any range); when the whole sub-block is excluded the service fails and the failure names the ranges. A configured port (Docker Compose, for example) is never moved. The service reports its address to the console when it joins. **Clean break:** a client set to an old port (the Gateway's 8815, the console's 8480 and the rest) finds nothing there and is set again ("Clean break" below). The Gateway keeps one listener per module (Flight, management, identity, apps, store, views, REST, MCP); only their ports move into its sub-block ("Not in this plan"). | A workstation with Hyper-V or WSL reserves port ranges that move at each boot; a fixed default inside one fails the start for no reason the person can see, and the console has to be told the address by hand. The old defaults (8470 to 8499, 8815 to 8822) sat among ports other software uses. |
| 7 | **`--skip-folder-permissions-check`** (and the setting `startup.skip_folder_permissions_check: true`) lets the folder step accept a folder whose owner or access list is not the strict one. It is available everywhere, including when registering a service (`service install`, `dbdatakit setup single-server`): using it is always the user's call. What the product decides is that it is visible: a warning at every start, a line in `service check`, and the server marked on the console's posture page. No rule refuses it under a service manager. | Developers, tests and test installs need to run a server on a folder the strict access lists would refuse; without a named switch the strict checks get loosened in code "just for this case", and stay loose. |
| 8 | **Pairing: the console (`dbdatakit`) is the only pairing host and the root of trust; every service shares one pairing library.** The direction reverses: the console generates, on its command line or in the web console, a command for the administrator to run on the remote machine, for example `dbdatarunner pair --host https://console:7800 --token <single-use> --console-thumbprint sha256:<hex>` (the console certificate's fingerprint, the algorithm part of the value). The token is single-use, short-lived and scoped to one product, a role ceiling and the group it joins; the thumbprint makes the first connection trusted. `pair` only records the join settings and (re)starts the service; the service joins at start as its own account, sends its address and certificate fingerprint, and the console calls back once to confirm and pins it. Users never copy codes between services except by running the generated command. All automation is a subcommand of our programs, never an OS-specific script. | Three ways to pair (a code made on the server, an announcement, a bundle token) with codes read out of program output by a script, and the pairing done by the elevated administrator rather than the service. The service is the one that must hold the pairing, so it is the one that makes it. |
| 9 | **Local automation: `dbdatakit setup single-server`** registers, starts and joins every suite service installed on this machine, through the shared service library (a library call, not a command-line call), each with a single-use join token in its settings; it waits and prints per-service progress (registered, started, listening and joined) and, for a service that fails to start, the contents of its startup-failure file. It ends with a summary and is safe to run again (it picks up where it stopped). `dbdatakit setup single-server --stop` stops every suite service on the machine. Docker Compose uses the same path (tokens through a shared volume, "Docker Compose" below). **`dbdatakit setup invite <gateway|runner|workspace>`** prints the command of decision 8 to run on a remote machine; the web console's Add a server screen does the same. | The install's pairing lived in a Scoop script, so it existed only for Scoop on Windows, and its failures surfaced as PowerShell exceptions. A subcommand is tested like the rest of our code and works the same in a container, on Linux and on Windows. |
| 10 | **Runner and Gateway groups never pair with each other directly.** The pairing token carries the group (a new Gateway group named X, or join Runner group Y); the console makes or hands over the group key during the join (the token proves the machine may have it). This replaces plan 28c E20d's join by a code made on the instance and E20f, and makes E20e unnecessary: the console holds every group key from the start. Linking a Runner group with a Gateway group stays a console action (plan 35 I4): the console gives each side the other's addresses and a link credential scoped to plan 35's three paths; a missing partner warns (plan 35 decision 7); a new instance of a linked group gets the link with its join. | E20e existed because a Gateway group made its own key, which the console then had to take over; E20f because joining another instance needed a second code read on that machine. If the console makes every key and the join token names the group, neither problem exists. |
| 11 | **Clean break:** existing installs and paired servers are paired again, and clients set to the old ports are set again. What `dbdatakit setup` and each service say to a server paired the old way, and to an old data folder, is a message naming the folder and what to do; never silent ("Clean break" below). | The old pairings and folders were made by the administrator's account; keeping them would keep the bug this plan removes. |
| 12 | **Order:** this plan comes before further work on the secrets review and plan 35 I4 to I9. It changes none of the secrets review's open decisions. | The secrets review and I4 to I9 build on how servers are paired and who holds the group keys; building them on the code pairing would build them twice. |
| 13 | **`doctor` is renamed `service check`** in every product (it checks the service's registration, settings and folder permissions); `health check` stays the question "does a running service answer". A repository test fails when any command or subcommand is named `doctor`, so it cannot come back (IS19). The owner has rejected the name `doctor` repeatedly: checks are `check`. | One word for one kind of command; a rejected name keeps coming back unless a test stops it. |
| 14 | **Console-started pairing for firewalls** where the server cannot reach the console (low priority, IS20). The console still generates the command for the remote, code included; it then either accepts the remote's address, or waits for the user to run the command there and enter the address the command prints; the console then connects to the server, much like today's path. | Decision 8 needs one call from the server to the console; some networks let only the console call out. |
| 15 | **A group key change** (a new key handed to every instance, what the old one protected re-encrypted) is not in plan 36: it is a row of plan 35 I8 or of the secrets review. | With the console holding every key from the start, E20e's take-over reason is gone; the remaining reason, a stolen machine, belongs with the secrets work. |
| 16 | **The Workspace's publisher keys** are handed by the console in plan 36 (IS14); `publisher pair --code` goes in IS18. | Otherwise one code pairing would outlive the plan that removes the others. |
| 17 | **The token on the command line** is acceptable: it is single-use and ends in 15 minutes by default; `--token -` reads it from standard input for whoever prefers that. | A command that can be copied whole keeps pairing one step. |
| 18 | **Linux service users:** one system user per service, named as the program (`dbdatagateway`), in the group `dbdatakit`, made by `install.sh --system`. | The same separation as Windows' per-service accounts, and one group for the shared logs folder (default 5). |

## Defaults taken while planning (owner not asked; each says where it is used)

1. **The library** is `Shared/src/DbDataKit.Hosting.Startup` (the sequence, the startup-failure file, the logs folder, the port block) and the join is part of
   `DbDataKit.Web` beside today's pairing (the token, the join exchange, the callback), so the products reference two libraries they mostly reference already. (IS01, IS09)
2. **One table of the suite's services** (`SuiteServices` in `DbDataKit.Hosting.Startup`): service name, account, description, program, data folder, log folder name,
   port sub-block, start order. The service command, `setup single-server`, `service check`, the Scoop binding and `check_default_ports.py` read it; nothing else spells a
   service's name. (IS03)
3. **The `<service>` of a log folder and of a failure file** is the program's name in lower case (`dbdatakit`, `dbdatagateway`, `dbdatarunner`,
   `dbdataworkspace`), the same on Windows and Linux and the same as the systemd unit. (IS01)
4. **The failure file's "not finished" line names the step** ("starting since 2026-11-17T09:12:03Z, step: certificate, not finished"), rewritten as each step of
   decision 3 begins, so a crash says how far it got. (IS01)
5. **How the shared logs folder is protected.** Windows: `logs` grants Administrators and SYSTEM full control, each suite service account "create files" and
   "create folders" on that folder only, and CREATOR OWNER full control on what is created, so a service owns what it makes and cannot open another's. A service
   that finds its own subfolder or failure file owned by another account writes nothing there and says so in the event log. Linux: `/var/log/dbdatakit` is
   `root:dbdatakit` mode `1770` (the sticky bit keeps a service from removing another's file), each service's user is in the group `dbdatakit`, each file it writes
   is mode `0600`; systemd's `LogsDirectory=dbdatakit/<service>` makes the subfolder with the service's user. (IS01, IS05)
6. **The top folder's owner is set to the Administrators group** by the install on every Windows edition (not left to the edition's default), and its access list
   grants the service account Modify. On Linux the unit's `StateDirectory=dbdatakit/<service>` makes it, owned by the service's user. (IS05)
7. **Finding a free port** is by binding: a port Windows excludes refuses the bind with access denied (WSAEACCES) and is skipped like a taken one; the failure file
   lists the excluded ranges when the whole sub-block is unusable (decision 6). The identity home on its own keeps a port in the console's sub-block. (IS08)
8. **The join token** is 128 random bits, shown once; the console keeps its hash, its product, role ceiling, group, who made it and when it ends (15 minutes by
   default, `--minutes` up to 24 hours). It is spent when the join completes (the console's callback confirmed); five failed tries cancel it. The generated
   command carries it as an argument; `--token -` reads it from standard input for whoever prefers that (decision 17). (IS09, IS10)
9. **`pair` writes one file, `join-request.yaml` (`join-request/1`), into the service's top folder**: the console's address, its thumbprint, the token, the role and the
   group. It is the one file an admin command puts inside a service's folder, it is read and deleted by the service at its next start, and the service keeps
   what the join gives it in its own settings. Decision 2 holds: the service never uses a file the administrator made, it consumes a request. (IS10)
10. **Docker Compose carries the tokens through a shared volume, not environment variables.** The console writes one `join-request.yaml` per member into a volume
    the members mount, at its first start, for the members its settings list; each member consumes its own. An environment variable would need the token before
    the console exists, so it would have to be a long-lived secret written in the compose file. (IS16)
11. **The server calls the console exactly once, to join** (and again only to join again after an unpair). Every other call is still the console calling the
    server, as D14 says; IS10 rewrites D14 in `docs/architecture.md` to say so. (IS10)

## How it works

### Startup (decision 3)

Every server's `server run` (as a service or not) runs `StartupSequence` before it serves anything:

| Step | What it does | What a failure says |
| --- | --- | --- |
| folder | the top folder exists and its owner and access list are as the install set them (or `--skip-folder-permissions-check` says otherwise); creates the subfolders it needs | the folder, its owner, the account the service runs as, and that `dbdatakit setup single-server` sets it up |
| keys | opens or makes the key ring and secret store as itself | the key file and which grant is unexpected |
| certificate | opens or makes its certificate | the file and the account |
| state | opens or makes the state database and runs its migrations | the engine, the database and the migration |
| ports | binds each listener: the configured port, or a pinned one, or a free one of its sub-block (decision 6) | the port, who holds it if Windows says, the excluded ranges |
| join | consumes `join-request.yaml` if there is one and joins the console (decision 8); otherwise checks it is paired | the console's address, what it answered |

The `logs` subfolder and the failure file come before the first step. A step that fails writes the file and the process exits non-zero (the service manager
records the failure); `service check` and `setup single-server` print the file.

### Skipping the folder permissions check (decision 7)

`--skip-folder-permissions-check` (on `server run`, `service install` and `dbdatakit setup single-server`, which record it in the registered service's settings) and
the setting `startup.skip_folder_permissions_check: true` let the folder step accept a folder whose owner or access list is not the strict one (for example a test's
temporary folder, a checkout, or a test install's data folder). It is accepted wherever it is given, under a service manager too: the user decides. Every start with
it writes a warning to the log, `service check` lists it, and the console's posture page shows the server with it.

### Pairing (decision 8)

```
console (CLI or Servers page)               remote machine (administrator)                 the service, as itself
 invite: product, role, group  ─────►  dbdatarunner pair --host ... --token ... --console-thumbprint ...
   keeps the token's hash                   writes join-request.yaml, (re)starts ─────►  startup step "join":
                                                                                           POST <host>/api/v1/join, TLS pinned to the thumbprint
                                                                                           {token proof, product, address, certificate fingerprint, public key}
   checks the token (product, group,  ◄────────────────────────────────────────────────
   expiry, unused), calls back the address
   with the fingerprint pinned  ─────────────────────────────────────────────────────►   answers the callback
   records the instance, spends the token,
   hands the group key and any link  ─────────────────────────────────────────────────►  keeps console key, group key, link; deletes the request
```

The token never travels in clear: the join carries a proof made from it, as today's pairing proof is made from a code. A wrong thumbprint, an expired or spent token,
another product or another group is refused, audited with who made the token and nothing of its value.

### `dbdatakit setup single-server` and `setup invite` (decision 9)

1. Finds the suite's programs installed beside `dbdatakit` (the folders of `SuiteServices`, the layout every package uses) and, for each, the service it
   should register.
2. For each, in start order: registers it through the shared service library if it is not (or re-registers it when it points elsewhere), sets the top folder's
   owner and access list, sets the program folder's access list (Administrators and SYSTEM full control, everyone else read and execute, inheritance cut, so a
   user cannot plant a file beside a program a service runs), mints a join token and writes the join request, starts the service.
3. Waits for each to be listening and joined, printing `registered · started · listening · joined` per service, and for a service that fails, the contents of
   `startup-failure-<service>.txt`.
4. Ends with a summary (what is ready, what is not, the console's address, the setup link of the first administrator from `FIRST-RUN.txt`). Run again, it skips
   what is done: a service registered here, running and joined is left alone.
5. `--stop` stops every suite service on the machine (before `scoop update`, for example).

`dbdatakit setup invite <gateway|runner|workspace> [--role <ceiling>] [--group <g>]` mints a join token and prints the command to run on the remote machine
(decision 8); the console's Add a server screen makes the same call.

### Scoop update (decision 1)

`docs/ci-lessons.md` records that `scoop update` refused while a service ran from the app's folder ("Running process detected, skip updating") **before any hook
ran**, which is why the copy exists today. The update is therefore: stop the services (`dbdatakit setup single-server --stop`, or `Stop-Service`), `scoop update -g
dbdatakit-full`, `dbdatakit setup single-server`; `post_install` and the manifest's notes print these commands. On a real uninstall, `pre_uninstall` stops the
services and prints the `sc.exe delete` commands that remove them.

Whether an update can be one command depends on two facts IS15 verifies against the Scoop version CI installs, and records in `docs/ci-lessons.md`:

- **Which `pre_uninstall` runs.** During `scoop update` the hook that runs is the one in the **installed** version's manifest, not the new one's. A check therefore
  installs a version N whose manifest carries the hook, then updates to N+1; a new manifest alone cannot show it.
- **The running-process check.** Scoop looks for processes running from the app's folder before it runs the hooks, unless the setting `ignore_running_processes` is
  set; the check verifies the order and what the setting changes.

If both allow it, `pre_uninstall` may stop the services itself and an update is `scoop update` followed by `dbdatakit setup single-server`; otherwise stopping
stays the user's first step. Copying the programs is not an option.

### Docker Compose (decision 9 and default 10)

The console's container lists its members in its settings (`setup.members`), writes their join requests into the shared volume `join` at its first start, and
each member's container consumes its own at its start. A port in the compose file is configured, so it is never moved (decision 6). The quickstart loses its
`instance pending` and `instance confirm` steps.

## Clean break (decision 11)

| What is found | Who says it | What it says |
| --- | --- | --- |
| a file inside a service's top folder, other than `join-request.yaml`, owned by another account than the service (the old install made it) | the service, at the folder step | the file, its owner, and: stop the service, move the folder aside, run `dbdatakit setup single-server` (an install made before plan 36 is set up again) |
| `server.yaml` listing a console paired the old way (a code, an announcement or the bundle) | the service at start (a warning), `service check`; the pairing is not used | the console and the command that pairs it again |
| `instances.yaml` with entries paired the old way | `dbdatakit setup single-server` (it removes them, naming each, and pairs this machine's services again), the Servers page (the entry says "paired the old way" with the generated command) | the server and the command |
| a service registered as `dbdatagateway` | `setup single-server` (it stops and removes it, registers `DbDataGateway` on the same folder, and says so) | both names |
| services registered from `%ProgramData%\DbDataKit\bin\<version>` | `setup single-server` (it registers them against the program folder and removes the copy, saying so) | the old and the new path |
| `bundle.json` in the console's folder, `--announce-listen` | the console refuses to start | the file or option and what replaced it (as plan 35 I2 refused the `runner:` section) |
| `DBDATAKIT_ANNOUNCE_URL` set | the service, a warning at every start | the variable, that it is not read, and how to remove it |
| a client, a connection string or an `instances.yaml` entry using an old default port (8480, 8482, 8471, 8495, 8815 to 8822) | nobody at run time (nothing listens there any more); the release notes and `docs/suite-single-server-install.md` list the old and the new defaults; the Servers page shows each server's reported address | the old and the new port |
| `service doctor` | the program (exit 2, `RemovedCommands`) | `service check` |
| `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` | the program (exit 2, `RemovedCommands`) | the sentence naming the new command |

## Formats and editor screens

- **`join-request/1`** (`join-request.yaml`): a new entry in `docs/formats.json`; `docs/ui-editors.md` row "none (a one-time file the `pair` command writes and
  the service deletes at its next start; the console's Servers page shows the join)". (IS10)
- **Pinned ports** are the existing listen settings of each server's settings file; if a key is added, the format's entry is bumped in the same slice. Editor:
  the server's settings screen edits them, the Servers page shows the address. (IS08)
- **`startup.skip_folder_permissions_check`**: a row "none (a switch the user sets by hand on purpose; `service check` and the posture page show it)". (IS07)
- **Join tokens** are rows of the console's state (a new table of the `trust` module, with its migration, `scripts/check_state_migrations.py`); the Servers page
  lists the waiting ones and cancels them. (IS10, IS11)
- **`bundle/1`** leaves `docs/formats.json` and `docs/ui-editors.md` with IS18.

## Slices

| Slice | What | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| IS01 | **The startup library** `DbDataKit.Hosting.Startup`: `StartupSequence` (the six steps of decision 3 as named steps a server fills in), the shared `logs` folder and its protection (default 5), `startup-failure-<service>.txt` (the "not finished" line with its step, deleted on success, the full reason on failure: account, folders, exception chain, hints for an excluded port), and the `SuiteServices` table's first columns. A library and its tests; no server uses it yet | none | M | `DbDataKit.Hosting.Startup.Tests` on Linux and Windows CI: a clean sequence leaves no file; a failing step writes the account, the folders and the exception chain; a process killed in a step leaves "step: <name>, not finished"; on Windows a second service account cannot open the first's file; on Linux the file is mode 0600 |
| IS02 | **The four servers start through it**: the console, Gateway, Runner and Workspace run their start as the sequence's steps; the Gateway's `StartFailure.cs` and `start-failure.txt` go (its test now looks in the shared path and says so); the event log line stays | IS01 | M | each server's test starts it with its port held by another listener and finds `startup-failure-<service>.txt` with the account, the folder and the exception; a clean start leaves none; Linux and Windows CI |
| IS03 | **Names, accounts and descriptions** (decision 4) from `SuiteServices`: `DbDataGateway` with `NT SERVICE\DbDataGateway`, the short descriptions, `dist.json` and the Scoop binding read the table; `setup single-server`'s later rename of a `dbdatagateway` registration is IS17 | IS01 | S | a shared test asserts the four names, accounts and the exact descriptions of decision 4; the Scoop e2e (`suite-install-windows`) reads `sc qc` and `sc qdescription` for each and finds them |
| IS04 | **Reproduce the workstation on CI**: a leg of the Windows install test that sets `nodefaultadminowner=1` and turns real-time scanning on (`docs/ci-lessons.md` says how) before installing, so an elevated administrator's objects are owned by the user as on Windows 11. Run once by hand (`workflow_dispatch`) on the code before IS05 to confirm the cause; required from IS05 | IS03 | S | the dispatched run on the code before IS05 shows the Gateway's `not owned by current user` (the run is linked in `docs/ci-lessons.md`), and without the setting the same code passes; if it does not reproduce, the row says what differs and the owner's workstation is the check by hand (its domain policy and security software are not on a hosted runner) |
| IS05 | **Services own their data** (decision 2): `service install` (and the MSIs and `install.ps1 -InstallService`) registers and creates the top folder with its owner and access list only (default 6); the Gateway's `EnsureStarter`, keys, certificate, state and configuration repository are made at the first start by the folder, keys, certificate and state steps; Linux units use `StateDirectory=` and `LogsDirectory=` | IS02, IS04 | M | the IS04 leg passes; a test lists every file under each data folder after the first start and finds each owned by the service's account (Windows leg) or user (Linux job: the server started as another user with `sudo -u`) |
| IS06 | **Admin commands through the local API** (decision 2): the local channel (`DbDataKit.Hosting.Local`) for all four servers; `secret`, `cert`, `state`, `setup link`, `user invite` and the rest call the running service; opening the files is `--recovery`, refused while the service runs, and a file it writes is given the service's account as owner. `WindowsServiceAccounts`' accepted grants (`e0135708`) are then only needed in recovery | IS05 | M | with the service running, `secret set` adds the value and no file under the data folder is owned by the administrator; a direct open while it runs is refused naming `--recovery`; recovery with the service stopped writes a file owned by the service's account; Windows leg and Linux job |
| IS07 | **`--skip-folder-permissions-check`** (decision 7): the flag on `server run`, `service install` and `setup single-server`, the setting, the warning at every start, `service check` and the posture page | IS02, IS19 | S | tests: started as a Windows service or a systemd unit (the detector replaced in the test) and from a terminal, it starts with the warning in the log; without it the same folder fails the folder step naming the flag; `service check` and the console's posture list name the server |
| IS08 | **Ports** (decision 6, default 7): the suite range 7800 to 7899 and the sub-blocks in `SuiteServices`; a free port of the sub-block at first start, pinned in the server's own settings; the Gateway's existing listeners moved into 7820 to 7839 (any free values, no fixed split); a configured port never moved; `check_default_ports.py` checks that the sub-blocks lie in the range, do not overlap and avoid Windows' default ports; the old defaults named in the release notes | IS02 | M | tests: the sub-block's first port held, the server takes the next and keeps it after a restart; a configured port held fails naming it; on the Windows runner a range added with `netsh int ipv4 add excludedportrange` is skipped and, covering the whole sub-block, is named in the failure file; the repository check's unit test |
| IS09 | **The join library** in `DbDataKit.Web`: the token (default 8: issue, hash, scope, expiry, single use, five tries), the join request and answer, the proofs, the console's callback and the pins both ways, the `join-request/1` file | IS01 | M | `DbDataKit.Web.Tests`: a join in one process succeeds once; a spent, expired, other-product or other-group token, a wrong thumbprint and a callback to an address that does not answer with the fingerprint are each refused and audited with no token value |
| IS10 | **Reversed pairing in the products** (decision 8): the console's `POST /api/v1/join` and `dbdatakit setup invite <gateway|runner|workspace> [--role <ceiling>] [--group <g>]` printing the command; `pair --host --token --console-thumbprint [--address]` in the Gateway, Runner and Workspace; the join step at start; `docs/architecture.md` D14 and the Web README say the server calls once to join. The old pairing stays beside it until IS18 | IS09, IS08 | L | Servers suite (SQLite, PostgreSQL, SQL Server): a console and a real Gateway, Runner and Workspace process each joined by its printed command, listed ready with the address and fingerprint they reported; a reused token is refused; the console's calls to each succeed pinned |
| IS11 | **The console's Add a server screen**: product, role ceiling, group; the command to copy; the tokens waiting, with Cancel | IS10 | S | Playwright story: make a command, run it against a test Runner, the server appears ready; vitest of the form |
| IS12 | **`dbdatakit setup single-server`** (decision 9): discovery, registration by library call, folder and program access lists, tokens, start, wait, progress, failure files, summary, run again safely; `--stop`; `--skip-folder-permissions-check` passed on to the registered services | IS10, IS05 | L | Windows CI (zips extracted, no Scoop) and the Linux job (systemd): every service registered, joined and listed; a second run says nothing to do; a service made to fail (its configured port held) prints its failure file and the summary names it, and a run after the fix finishes it |
| IS13 | **Groups through the token** (decision 10): `--group` new or existing in the token; the console makes a group key when it makes the group and hands it at the join; a Gateway no longer makes its own key; the join answer carries a link when plan 35 I4 has made one. Replaces E20d's join by a code (`POST /api/instances/{id}/join` stays until IS18) | IS10 | M | Servers suite: two Gateway instances joined to group X serve each other's sessions with no key copied; a Runner instance joined to group Y resolves the group's logins; a token for Y used by a Gateway, or for another group, is refused and sends nothing |
| IS14 | **Server-to-server trust through the console**: the Workspace's publisher key on a Gateway or a Runner is handed by the console (the Workspace never pairs with a code), decision 16 | IS10 | M | the Workspace publishes to a Gateway and a Runner with no code typed; `publisher pair --code` answers the sentence |
| IS15 | **The Scoop scripts shrink** (decision 1): no copy; `post_install` prints the next commands; `pre_uninstall` stops the services and on an uninstall prints the `sc.exe delete` commands; verify, by installing a version N whose manifest carries the hook and updating to N+1, which `pre_uninstall` runs during `scoop update` and whether Scoop's running-process check (and `ignore_running_processes`) comes before it; if both allow it, `pre_uninstall` stops the services on an update too; the outcome in `docs/ci-lessons.md`; `dist/README.md` and `docs/suite-single-server-install.md` rewritten | IS12 | M | `suite-scoop-e2e.ps1`: install, `dbdatakit setup single-server`, every service ready and registered against `current`; an update from N to N+1 by the verified procedure ends with the new version running; uninstall leaves no service running |
| IS16 | **Docker Compose** through the same path (default 10) | IS12 | S | `dist/compose/check.sh` on the quickstart: the console and the Gateway are joined with no `instance confirm`; CI runs it |
| IS17 | **Clean break messages** (decision 11, the table above) | IS12, IS13 | S | one test per row of the table: each finding gives its message, exit code or warning |
| IS18 | **Delete the old pairing and install code** (adapter first, delete second): `PairingCodes`, `pairing-codes.json`, the `server pair` code verbs, `PairingClient`'s code exchange, `instance add --code`, the announcement (`ConsoleAnnouncer`, `--announce-listen`, `DBDATAKIT_ANNOUNCE_URL`, `ANNOUNCEURL`, `-AnnounceUrl`, `--announce-url`), the bundle (`bundle/1`, `bundle init`), E20d's join route, `publisher pair --code` | IS11, IS13 to IS17 | M | the suites pass with those types gone; the removed verbs answer their sentence (IS17's tests); `check_formats.py` and `check_ui_editors.py` pass without `bundle` |
| IS19 | **`doctor` becomes `service check`** (decision 13): the verb renamed in the shared `ServiceCommand` and `HostingNouns`, the Gateway's `ServiceCli` and `Cli`, and every product's README and docs; `service doctor` answers the sentence naming `service check`; a repository check (`scripts/check_command_names.py` with its unit test in `scripts/tests`, or a test over the products' command trees) fails when any command or subcommand is named `doctor` | none | S | the check's unit test fails on a command tree with a `doctor` verb and passes on the repository; the products' CLI tests call `service check`; `check_docs_commands.py` passes |
| IS20 | **Console-started pairing** (decision 14, low priority): `setup invite` (and the Add a server screen) offers it; the generated command carries the code; the console either takes the remote's address up front or waits for the user to run the command there and enter the address it prints; it then connects to the server and pins it, as today's code pairing does, with the token's scope and single use | IS10, IS11 | M | Servers suite: a Runner whose calls to the console are blocked joins by this path and is listed ready; a reused or expired code is refused; a Playwright story of entering the address |

Order: IS19 any time (before IS07 and IS12, which use `service check`); IS01, IS02, IS03, IS04, IS05, IS06; IS07 and IS08 any time after IS02; IS09 after IS01;
IS10 after IS08 and IS09; then IS11, IS12 and IS13 (any order), IS14 after IS10; IS15 and IS16 after IS12; IS17; IS18 after them; IS20 (low priority) last. The
commit of whichever slice lands last turns the plan into documentation (the Hosting.Startup README, `docs/architecture.md`, `dist/README.md`) and deletes it.

## What it replaces

- **Plan 28c E20d** (built): its join of another instance by a code made on that instance gives way to IS13; the console's group key store, `GroupKeyApi` and
  `RunnerGroupSecrets` stay. **E20e** and **E20f** are no longer rows: the console holds every group key from the start (IS13) and a join is one command (IS10).
- **Plan 35 I4** builds the link between a Gateway group and a Runner group on IS13: the console holds both keys and hands a new instance of a linked group its link
  at the join.
- **Plan 33 SC10, SC12, SC13 and plan 23a FQ18** (built): the announcement, the Scoop scripts' pairing and the bundle are replaced by IS10, IS12 and IS15 and
  deleted in IS18; `dist/README.md`, "One package for a single server", changes with IS15.

## Not in this plan

Gateway work, to be placed by the plans review in progress:

- **One public HTTPS port for the Gateway, by path**, beside Flight and management (and a PostgreSQL wire listener later), in place of today's listener per module
  (Flight, management, identity, apps, store, views, REST, MCP). Plan 36 only moves the existing listeners into the Gateway's sub-block (IS08).
- **Several sites with different host names** on one Gateway.

## Questions for the owner

None open: the nine questions of the first draft were answered on 2026-11-17 (the command's name and Scoop updates: decisions 1 and 9; the folder check under a
service manager: decision 7; a whole port block excluded: decision 6; a server that cannot reach the console: decision 14; the rest: decisions 15 to 18).
