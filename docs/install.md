# Install, startup and pairing

How the four services (console, Gateway, Runner, Workspace) are installed, start and join the console. This page is the design the install is being
built to; the work and what exists today are in [planning/INSTALLER.md](../planning/INSTALLER.md). Until a part is built, `dist/README.md` and
`docs/suite-single-server-install.md` describe the install as it is.

The rule behind every part: **a file is created and used by one account only**. On a Windows client an object an elevated administrator creates is owned
by that user, on Windows Server by the Administrators group; a service that then opens it as `NT SERVICE\<name>` fails (git refuses a repository "not
owned by current user", a key file carries an unexpected grant). If only the service ever creates its files, the owner and the grants are the same on a
client and on Server.

## A service owns its data

The install only registers the service and creates its top folder with its access list. Everything inside (subfolders, keys, certificate, state, the
configuration repository, logs) is created at first start by the service's own account. An admin command that needs a service's data goes through the
service's local API (a named pipe or Unix socket); opening the files directly is a recovery mode, allowed only while the service is stopped.

Example: `dbdatagateway secret set warehouse` with the service running asks the service to store the value; no file in its folder is touched by the
administrator's account. With the service stopped, `secret set --recovery` writes the file and gives it the service's account as owner.

## One startup sequence

Every server's `server run` (as a service or not) runs the same steps before it serves anything, from one shared library, on Windows and Linux: settle the
folder and its permissions, then keys, then the certificate, then the state and its migrations, then bind the ports, then join the console. A failure in a
step looks the same in every product, and a fix reaches all four.

| Step | What it does | What a failure says |
| --- | --- | --- |
| folder | the top folder exists and its owner and access list are as the install set them (or `--skip-folder-permissions-check` says otherwise); creates the subfolders it needs | the folder, its owner, the account the service runs as, and that `dbdatakit setup single-server` sets it up |
| keys | opens or makes the key ring and secret store as itself | the key file and which grant is unexpected |
| certificate | opens or makes its certificate | the file and the account |
| state | opens or makes the state database and runs its migrations | the engine, the database and the migration |
| ports | binds each listener: the configured port, or a pinned one, or a free one of its sub-block ("Ports" below) | the port, who holds it if Windows says, the excluded ranges |
| join | consumes `join-request.yaml` if there is one and joins the console ("Pairing" below); otherwise checks it is paired | the console's address, what it answered |

The `logs` subfolder and the failure file come before the first step. A step that fails writes the failure file and the process exits non-zero (the
service manager records the failure); `service check` and `setup single-server` print the file.

Example: the Gateway's service starts on a folder an administrator's account made before this design. It stops at the folder step, and
`C:\ProgramData\DbDataKit\logs\startup-failure-dbdatagateway.txt` names the folder, its owner, the account `NT SERVICE\DbDataGateway`, and that
`dbdatakit setup single-server` sets it up; `services.msc` shows a failed start, and `setup single-server` prints the same file.

## Logs and the startup-failure file

Logs are in one place: `C:\ProgramData\DbDataKit\logs\<service>\` (Linux `/var/log/dbdatakit/<service>/`). A failed start is reported in
`C:\ProgramData\DbDataKit\logs\startup-failure-<service>.txt`: at each start the service writes "starting since <time>, step: <step>, not finished",
rewritten as each step begins ("starting since 2026-11-17T09:12:03Z, step: certificate, not finished"), so a crash says how far it got; it deletes the
file when startup succeeds; when startup fails it writes the full reason (the account, the folders, the exception chain, hints such as Windows' excluded
port ranges). No file means a clean start; a leftover "not finished" line means a crash the service could not report. The install creates `logs`; each
service account creates and owns only its own subfolder and its own failure file; no service reads another's. Installers and `setup` print the file, so
"Cannot start service" always comes with its reason.

## Service names, accounts and descriptions

The Windows services are exactly `DbDataKit`, `DbDataGateway`, `DbDataRunner` and `DbDataWorkspace`, each running as `NT SERVICE\<that name>`. Their
descriptions:

- `DbDataKit`: "DbDataKit console: sign-in, users and management of the Gateway, Runner and Workspace servers."
- `DbDataGateway`: "DbDataKit gateway: serves data, semantic models, master data, apps, dashboards and reports to people, applications and agents."
- `DbDataRunner`: "DbDataKit runner: runs published tasks (data loads, replication, deployments and quality checks) on schedule or on demand."
- `DbDataWorkspace`: "DbDataKit workspace: where people and agents author, test and publish apps, data models, replications and master data."

On Linux each service has a system user named as the program (`dbdatagateway`) in the group `dbdatakit`, made by `install.sh --system`; the group owns the
shared logs folder.

## Ports

The suite uses one range, **7800 to 7899**, outside Windows' default ports (80, 443, 135, 445, 3389, 5040, 5357, 5985, 5986, 7680, 47001, the dynamic
range 49152 to 65535) and common developer ports. Each service has a sub-block and a default: the console 7800 (7800 to 7819), the Gateway 7820 to 7839,
the Runner 7840 (7840 to 7859), the Workspace 7860 (7860 to 7879); 7880 to 7899 are spare. With no port configured, a service takes a free port of its
sub-block at first start and pins it in its own settings. Windows' excluded port ranges (Hyper-V and WinNAT reserve ranges that move at boot) are skipped;
when the whole sub-block is excluded the service fails and the failure file names the ranges. A configured port (Docker Compose, for example) is never
moved. The service reports its address to the console when it joins.

Example: on a workstation with WSL, 7840 is inside a reserved range; the Runner takes 7841, pins it, and the console's Servers page shows
`https://host:7841`.

## Skipping the folder permissions check

`--skip-folder-permissions-check` (and the setting `startup.skip_folder_permissions_check: true`) lets the folder step accept a folder whose owner or
access list is not the strict one: a test's temporary folder, a checkout, a test install. It is accepted everywhere, under a service manager too: the user
decides. It is always visible: a warning at every start, a line in `service check`, and the server marked on the console's posture page. Without a named
switch, strict checks get loosened in code "just for this case" and stay loose.

## Pairing

The console is the only pairing host and the root of trust; every service uses one pairing library. The console generates, on its command line or in
the web console, a command for the administrator to run on the remote machine:

```
dbdatarunner pair --host https://console:7800 --token <single-use> --console-thumbprint sha256:<hex>
```

The token is single-use, short-lived (15 minutes by default) and scoped to one product, a role ceiling and the group it joins; the thumbprint makes the
first connection trusted. `pair` only records the join settings and (re)starts the service; the service joins at start as its own account, sends its
address and certificate fingerprint, and the console calls back once to confirm and pins it. A server calls the console once, to join; every other call is
the console calling the server. Users never copy codes between services except by running the generated command. `--token -` reads the token from
standard input for whoever prefers that. All automation is a subcommand of our programs, never an OS-specific script.

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

**The token never travels in clear.** The join carries a proof made from it, as a pairing proof was made from a code, over TLS pinned to the console's
thumbprint, so a machine in the middle neither learns the token nor passes for the console. A wrong thumbprint, an expired or spent token, another product
or another group is refused, and audited with who made the token and nothing of its value.

**Join tokens are rows of the console's state**: a table of the `trust` module, added by its own migration, holding the token's hash, its product, role
ceiling and group, who made it and when it ends. The Servers page lists the tokens still waiting and cancels them.

**Groups.** Runner and Gateway groups never pair with each other directly. The token names the group (a new Gateway group X, or join Runner group Y);
the console makes the group key when it makes the group and hands it over during the join. Linking a Gateway group with a Runner group is a console
action (`docs/architecture.md`, "Isolation by trust"); a new instance of a linked group gets the link with its join.

**Publisher keys.** The Workspace's publisher key on a Gateway or a Runner is handed over by the console; the Workspace never pairs with a code.

**Networks where a server cannot reach the console.** The console can start the pairing instead. It still generates the command for the remote machine,
code included; it then either takes the remote's address up front, or waits for the user to run the command there and enter the address the command
prints. The console then connects to the server and pins it, with the same token scope and single use. Example: a Runner in a network that the console
can reach but that cannot reach the console's 7800; the administrator runs the generated command on it, types the address it prints into the console's
Add a server screen, and the Runner is listed ready.

## Setting up one machine, or inviting another

`dbdatakit setup single-server` registers, starts and joins every suite service installed on the machine, through the shared service library (a library
call, not a command line), each with a single-use join token in its settings. It prints per-service progress (registered, started, listening, joined)
and, for a service that fails to start, its startup-failure file; it ends with a summary and is safe to run again. `dbdatakit setup single-server --stop`
stops every suite service on the machine. Docker Compose uses the same path, with the tokens passed through a shared volume.

`dbdatakit setup invite <gateway|runner|workspace>` prints the pairing command for a remote machine; the console's Add a server screen does the same.

## Scoop

Scoop installs no service by itself: the services are registered against Scoop's `current` folder, which Scoop repoints on update, and no program files
are copied. `post_install` (and the manifest's notes) print the next commands: `dbdatakit setup single-server`, and how to update. On an uninstall
`pre_uninstall` stops the services and prints the `sc.exe delete` commands that remove them. No other service command is run by the Scoop scripts: what
runs as an elevated script inside another program cannot be tested like our own code, so the script stays this small.

**Updating.** Scoop refuses to update an app while a process runs from its folder ("Running process detected, skip updating"), and it does so **before
any hook runs**; that refusal is why the package once copied the programs to `%ProgramData%\DbDataKit\bin\<version>` and ran the services from the copy.
Copying the programs is not an option, so an update is three commands:

```
dbdatakit setup single-server --stop     # or Stop-Service on each service
scoop update -g dbdatakit-full
dbdatakit setup single-server
```

Whether it can become one command depends on two facts about Scoop:

- **Which `pre_uninstall` runs.** During `scoop update` the hook that runs is the one in the **installed** version's manifest, not the new one's. A new
  manifest alone therefore cannot show what an update does: the check installs a version N whose manifest carries the hook, then updates to N+1.
- **The running-process check.** Scoop looks for processes running from the app's folder before it runs the hooks, unless Scoop's setting
  `ignore_running_processes` is set.

If both allow it, `pre_uninstall` stops the services itself and an update is `scoop update` followed by `dbdatakit setup single-server`; otherwise
stopping the services stays the user's first step. What the check found, against the Scoop version CI installs, is recorded in `docs/ci-lessons.md`.

## Commands named `check`

`service check` checks a service's registration, settings and folder permissions; `health check` asks whether a running service answers. No command or
subcommand is named `doctor`; a repository check keeps it so.

## Clean break

Installs made before this design are set up again: existing pairings, data folders made by the administrator's account, and clients set to the old
ports. Keeping the old pairings and folders would keep the bug this design removes. Nothing is silent: each thing an old install leaves behind is found
by someone, who says what it found and what to do.

| What is found | Who says it | What it says |
| --- | --- | --- |
| a file inside a service's top folder, other than `join-request.yaml`, owned by another account than the service (the old install made it) | the service, at the folder step | the file, its owner, and: stop the service, move the folder aside, run `dbdatakit setup single-server` (an install made before this design is set up again) |
| `server.yaml` listing a console paired the old way (a code, an announcement or the bundle) | the service at start (a warning), `service check`; the pairing is not used | the console and the command that pairs it again |
| `instances.yaml` with entries paired the old way | `dbdatakit setup single-server` (it removes them, naming each, and pairs this machine's services again), the Servers page (the entry says "paired the old way" with the generated command) | the server and the command |
| a service registered as `dbdatagateway` | `setup single-server` (it stops and removes it, registers `DbDataGateway` on the same folder, and says so) | both names |
| services registered from `%ProgramData%\DbDataKit\bin\<version>` | `setup single-server` (it registers them against the program folder and removes the copy, saying so) | the old and the new path |
| `bundle.json` in the console's folder, `--announce-listen` | the console refuses to start | the file or option and what replaced it (as the console refuses the removed `runner:` section) |
| `DBDATAKIT_ANNOUNCE_URL` set | the service, a warning at every start | the variable, that it is not read, and how to remove it |
| a client, a connection string or an `instances.yaml` entry using an old default port (console 8480, identity home 8482, Workspace 8471, Runner 8495, Gateway 8815 to 8822) | nobody at run time (nothing listens there any more); the release notes and `docs/suite-single-server-install.md` list the old and the new defaults; the Servers page shows each server's reported address | the old and the new port |
| `service doctor` | the program (exit 2, `RemovedCommands`) | `service check` |
| `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` | the program (exit 2, `RemovedCommands`) | the sentence naming the new command |

Example: an administrator upgrades a machine whose Gateway was registered as `dbdatagateway` from `%ProgramData%\DbDataKit\bin\<version>` and paired by
a code. `dbdatakit setup single-server` says it removed `dbdatagateway` and registered `DbDataGateway` on the same folder, registered it against the
program folder and removed the copy, and removed the old `instances.yaml` entry and joined the Gateway again; the Gateway's folder step then names any file
the old install's administrator made and says to move the folder aside and run `setup single-server` again.
