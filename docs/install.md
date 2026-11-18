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

Every server runs the same steps before it serves anything, from one shared library, on Windows and Linux: settle the folder and its permissions, then
keys, then the certificate, then the state and its migrations, then bind the ports, then join the console. A failure in a step looks the same in every
product, and a fix reaches all four.

## Logs and the startup-failure file

Logs are in one place: `C:\ProgramData\DbDataKit\logs\<service>\` (Linux `/var/log/dbdatakit/<service>/`). A failed start is reported in
`C:\ProgramData\DbDataKit\logs\startup-failure-<service>.txt`: at each start the service writes "starting since <time>, not finished"; it deletes the
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

**Groups.** Runner and Gateway groups never pair with each other directly. The token names the group (a new Gateway group X, or join Runner group Y);
the console makes the group key when it makes the group and hands it over during the join. Linking a Gateway group with a Runner group is a console
action (`docs/architecture.md`, "Isolation by trust"); a new instance of a linked group gets the link with its join.

**Publisher keys.** The Workspace's publisher key on a Gateway or a Runner is handed over by the console; the Workspace never pairs with a code.

**Networks where a server cannot reach the console.** The console can start the pairing instead: it generates the command, takes or waits for the
remote's address, and connects to the server, pinning it with the same token scope and single use.

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
ports (console 8480, identity home 8482, Workspace 8471, Runner 8495, Gateway 8815 to 8822). A server paired the old way, an old data folder, an old
service name or a removed command (`server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init`,
`service doctor`) gives a message naming what it found and what to do; never silent. Keeping the old pairings and folders would keep the bug this design
removes.
