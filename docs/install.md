# Install, startup and pairing

How the four services (console, Gateway, Runner, Workspace) are installed, start and join the console. This page is the design the install is being
built to; the work and what exists today are in [planning/INSTALLER.md](../planning/INSTALLER.md). Until a part is built, `dist/README.md` and
`docs/suite-single-server-install.md` describe the install as it is.

The rule behind every part: **a file is created and used by one account only**. On a Windows client an object an elevated administrator creates is owned
by that user, on Windows Server by the Administrators group; a service that then opens it as `NT SERVICE\<name>` fails (git refuses a repository "not
owned by current user", a key file carries an unexpected grant). If only the service ever creates its files, the owner and the grants are the same on a
client and on Server.

Every program of the suite needs the .NET 10 runtime installed on the machine; the four services need ASP.NET Core with it.

What an administrator sets up and checks so that each service's files stay its own is in [install-hardening.md](install-hardening.md).

## A service owns its data

The install only registers the service and creates its top folder with its access list. Everything inside (subfolders, keys, certificate, state, the
configuration repository, logs) is created at first start by the service's own account. An admin command that needs a service's data goes through the
service's local API (a named pipe or Unix socket); opening the files directly is a recovery mode, allowed only while the service is stopped.

On Windows, `service install` (which the MSI, `install.ps1 -InstallService` and the `dbdatakit-full` Scoop package run) creates the top folder, gives
it the Administrators group as owner and grants the service's account Modify on it, with inheritance from the parent cut. Without the explicit owner,
a Windows client would make the administrator who ran the install the owner, and Windows Server the Administrators group. The `logs` folder beside the
top folders is made by the first service that starts ("Logs and the startup-failure file").

Example: right after `dbdatagateway service install`, `C:\ProgramData\DbDataKit\gateway` is empty and owned by `BUILTIN\Administrators`. At its first
start the Gateway writes `config\server.yaml` (only `schemaVersion: 2`), its keys, its certificate and its state there, each owned by
`NT SERVICE\DbDataGateway`.

On Linux, `install.sh --system` makes a system user for the service, named as the program (`dbdatagateway`), in the group `dbdatakit`, and writes the
service's unit to `/etc/systemd/system/<program>.service`. The unit runs as that user. At each start systemd makes the service's data folder
(`StateDirectory=`, `/var/lib/dbdatakit/gateway`) and logs folder (`LogsDirectory=`, `/var/log/dbdatakit/dbdatagateway`), owned by that user with mode
`0700`. The install itself makes nothing under `/var/lib/dbdatakit` or `/var/log/dbdatakit`. Running `install.sh --system` again changes no user,
group or unit that is already right.

```
$ sudo ./install.sh --system
Installing DbDataGateway 2026.11.21.1200 to /opt/dbdatagateway
Made the system group dbdatakit.
Made the system user dbdatagateway in the group dbdatakit.
Wrote the unit /etc/systemd/system/dbdatagateway.service: the service runs as dbdatagateway.
```

Example: `dbdatagateway secret set warehouse` with the service running asks the service to store the value; no file in its folder is touched by the
administrator's account. With the service stopped, `secret set --recovery` writes the file and gives it the service's account as owner.

## The Gateway's configuration repository

The Gateway keeps its settings in a git repository, the `config` folder of its data folder, which it makes at its first start. Git refuses a repository
whose folder another account owns. When git refuses `config` for its owner, the Gateway does not start: the start-failure file names the folder, its
owner, the account the Gateway runs as and the fix. This happens when another account made the folder, for example an administrator's command run
before the service first started, or a folder copied from another machine.

Example: `C:\ProgramData\DbDataKit\gateway\config` is owned by `CONTOSO\ada`. The start-failure file says: `The configuration repository
'C:\ProgramData\DbDataKit\gateway\config' cannot be opened: 'C:\ProgramData\DbDataKit\gateway\config' is owned by CONTOSO\ada, and
NT SERVICE\DbDataGateway, the account this runs as, does not own it, so git refuses the repository. As an administrator, make NT SERVICE\DbDataGateway
the owner of the folder and what is in it (icacls "C:\ProgramData\DbDataKit\gateway\config" /setowner "NT SERVICE\DbDataGateway" /T /C), then start
it again.`

## One startup sequence

Every server's `server run` (as a service or not) runs the same steps before it serves anything, from one shared library, on Windows and Linux: settle the
folder and its permissions, then keys, then the certificate, then the state and its migrations, then bind the ports, then join the console. A failure in a
step looks the same in every product, and a fix reaches all four. The library is `Shared/src/DbDataKit.Hosting.Startup`; the join it ends with lives in `DbDataKit.Web`. The library reads one table, `SuiteServices` (each service's name, account, description, program, data and log folders, port sub-block and start order), the only place a service's name is spelled.

| Step | What it does | What a failure says |
| --- | --- | --- |
| folder | the top folder exists and its owner and access list are as the install set them (or `--skip-folder-permissions-check` says otherwise); creates the subfolders it needs | the folder, its owner, the account the service runs as, and that `dbdatakit setup single-server` sets it up |
| keys | opens or makes the key ring and secret store as itself | the key file and which grant is unexpected |
| certificate | opens or makes its certificate | the file and the account |
| state | opens or makes the state database and runs its migrations | the engine, the database and the migration |
| ports | binds each listener: the configured port, or a pinned one, or a free one of its sub-block ("Ports" below) | the port, who holds it if Windows says, the excluded ranges |
| join | consumes `join-request.yaml` if there is one and joins the console ("Pairing" below); otherwise checks it is paired | the console's address, what it answered |

A Gateway with a join request waiting joins at the start of its ports step, before its listeners start: a listener of its own on the management API's
address, with the certificate the management API presents, answers the console's call back and then stops. The Gateway then starts its listeners on the
ports and with the key the join handed over, and an instance of a group serves the group's certificate. A refused join still fails the join step.

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
port ranges). No file means a clean start; a leftover "not finished" line means a crash the service could not report. The first service to start creates `logs` with the protection below; each
service account creates and owns only its own subfolder and its own failure file; no service reads another's. Installers and `setup` print the file, so
"Cannot start service" always comes with its reason.

Where a server keeps its logs and its failure file depends on its data folder:

- The data folder is the service's folder of an install (`C:\ProgramData\DbDataKit\runner`, Linux `/var/lib/dbdatakit/runner`): the shared
  `logs` folder above (Linux `/var/log/dbdatakit`).
- The data folder has the service's folder name under another root (`D:\suite\runner`): a `logs` folder beside it (`D:\suite\logs`).
- Any other data folder, such as a run from a terminal on a folder of its own or a test's folder: a `logs` folder inside it. Such a run never writes
  to the machine's logs folder.

Example: when `dbdatarunner server run --repo C:\dev\runner-dev` fails at the ports step, it writes
`C:\dev\runner-dev\logs\startup-failure-dbdatarunner.txt`. On Linux, `--repo /home/ana/runner-dev` writes
`/home/ana/runner-dev/logs/dbdatarunner/startup-failure-dbdatarunner.txt`.

`<service>` in these names is the program in lower case (`dbdatagateway`), the same as its systemd unit. On Windows the `logs` folder gives Administrators and SYSTEM full control, each service account "create files" and "create folders" on that folder only, and CREATOR OWNER full control, so each service owns what it creates there and nothing else; on Linux systemd creates each service's `/var/log/dbdatakit/<service>/` when it starts the unit (`LogsDirectory=`), owned by the service's user with mode `0700`, and the failure file is `/var/log/dbdatakit/<service>/startup-failure-<service>.txt`, mode `0600`. A service that finds its own subfolder or failure file owned by another account writes nothing there and says so in the event log.

## Service names, accounts and descriptions

The Windows services are exactly `DbDataKit`, `DbDataGateway`, `DbDataRunner` and `DbDataWorkspace`, each running as `NT SERVICE\<that name>`. Their
descriptions:

- `DbDataKit`: "DbDataKit console: sign-in, users and management of the Gateway, Runner and Workspace servers."
- `DbDataGateway`: "DbDataKit gateway: serves data, semantic models, master data, apps, dashboards and reports to people, applications and agents."
- `DbDataRunner`: "DbDataKit runner: runs published tasks (data loads, replication, deployments and quality checks) on schedule or on demand."
- `DbDataWorkspace`: "DbDataKit workspace: where people and agents author, test and publish apps, data models, replications and master data."

Each Windows service's display name, the name `services.msc` lists, is its service name (`DbDataRunner`).

On Linux each service has a system user named as the program (`dbdatagateway`) in the group `dbdatakit`, made by `install.sh --system`. Its systemd
unit is named as the program too (`dbdatarunner.service`), and the unit's `Description=` is the service's description above. Example: `systemctl
status dbdatarunner` shows `dbdatarunner.service - DbDataKit runner: runs published tasks (data loads, replication, deployments and quality checks)
on schedule or on demand.`

Each server's `service install` registers its Windows service under that name and account, with that description. The names and descriptions are
written once, in `SuiteServices` (`Shared/src/DbDataKit.Hosting.Startup/SuiteServices.cs`), and the MSI, `install.ps1` and the `dbdatakit-full` Scoop
package take the service names from there too. Example: `sc qdescription DbDataRunner` prints the runner's description above, and `dbdatagateway service install` registers `DbDataGateway`
running as `NT SERVICE\DbDataGateway`.

Windows compares service names without case, and makes a virtual account's SID from the upper-case name. A Gateway registered as `dbdatagateway` by an
earlier release is therefore the same service, and `NT SERVICE\DbDataGateway` is the account it already runs as: its key files and folders stay readable
to it.

## Ports

The suite uses one range, **7800 to 7899**, outside Windows' default ports (80, 443, 135, 445, 3389, 5040, 5357, 5985, 5986, 7680, 47001, the dynamic
range 49152 to 65535) and common developer ports. Each service has a sub-block: the console 7800 to 7819 (default 7800), the Gateway 7820 to 7839, the
Runner 7840 to 7859 (default 7840), the Workspace 7860 to 7879 (default 7860); 7880 to 7899 are spare. With no port configured, a service takes a free
port of its sub-block at first start and pins it in its own settings. Windows' excluded port ranges (Hyper-V and WinNAT reserve ranges that move at boot)
are still skipped, because a machine whose dynamic range was lowered can reserve inside any range; when the whole sub-block is excluded the service fails
and the failure file names the ranges. A configured port (Docker Compose, for example) is never moved. A free port is found by binding it, which skips a port another program holds. The service reads the excluded ranges from Windows at each start, because Windows can let a loopback listener bind inside an excluded range. The identity home, when it runs on its own, keeps a port in the console's sub-block. The service reports its address to the console
when it joins.

Where the console, the Runner and the Workspace take their port from, at each start:

- `--listen` is given on `server run`: that address, as given. It is never written to a file.
- The server's settings file has a `listen:` line: that address. The file is `settings.yaml` in the console's folder, and `server.yaml` in the
  Runner's and the Workspace's.
- Neither: the first port of the service's sub-block that can be bound, on `localhost` for the console and on `127.0.0.1` for the Runner and the
  Workspace. Once the server listens on it, the server writes it as the `listen:` line, so every later start uses the same port.

A port from `--listen` or from the `listen:` line that another program holds fails the ports step, and the failure file names the port. The server
never takes another one. To move a server, change its `listen:` line and restart it. The `listen:` line a first start writes:

```yaml
# The address this server listens on, chosen from its ports at its first start. Change it to move the server; --listen overrides it.
listen: https://127.0.0.1:7841
```

Where `health check` asks, with no `--url`:

- The server's settings file has a `listen:` line: that address. A server that listens on `0.0.0.0`, `::` or a host name is asked on `localhost`.
  `--repo` names the server's folder when it is not the default one.
- The file or its `listen:` line is not there: the default address (`https://localhost:7800`, `:7840` or `:7860`).
- The file cannot be read, or its `listen:` line is not an address: the check asks nothing and exits 1. It names the file and `--url`, because
  another program could answer on the default address and the check would then report the wrong server.

The Gateway's `health check` asks its `listen` address from `config/server.yaml` in its data folder, which `--repo` names in the same way.

Example: on a Runner whose `listen:` line is `https://127.0.0.1:7841`, `dbdatarunner health check` asks `https://127.0.0.1:7841/health/live` and
prints `healthy: HTTP 200`. On Linux the Runner's folder is closed to every user but the service's, so the same command run by another user
prints:

```
The address the server listens on cannot be read: Access to the path '/var/lib/dbdatakit/runner/server.yaml' is denied. Fix the listen: line, or give the address with --url.
```

Run it with `sudo`, or give the address: `dbdatarunner health check --url https://127.0.0.1:7841/health/live`.

`dbdatakit setup link` and `user invite` start their links with the console's `listen:` address when `--url` is not given. The identity home on its
own (`dbdatakit identity run`) listens on 7819 unless `--listen` says otherwise.

Example: on a workstation with WSL, 7840 is inside a reserved range; the Runner takes 7841, pins it, and the console's Servers page shows
`https://host:7841`.

**The `listen` setting.** Every service and every listener of the Gateway has one setting, `listen`: an address written the way Kestrel writes it, with its
scheme, address and port, or a list of addresses.

```yaml
listen: https://127.0.0.1:7841                       # one address
listen: [https://0.0.0.0:7841, https://127.0.0.1:9443]   # a list: the server listens on each
```

- The address is `https://0.0.0.0:7841`, `https://127.0.0.1:9443`, `https://[::1]:9443`, `https://localhost:7800` or `https://*:7841`. A bare port, a
  host name with no port or an address with a path is refused, naming the file and the setting.
- `https` uses the service's certificate. The Gateway's Flight SQL listener is the one that takes `http`, and only when `tls.mode` is `None`.
- A list and a single address mean the same thing in every file, on the command line (`--listen https://127.0.0.1:7841;https://127.0.0.1:9443`) and in an
  environment variable. The first address of a list is the one a server reports to the console.
- A file with `port` or `address` in place of `listen` is refused naming the file and the setting; there is no other spelling.

**The Gateway's listeners.** A Gateway has three listeners by default: the public HTTPS listener (`public.listen`), Flight SQL (`listen`) and the management
API (`management.listen`, on the loopback address). None has a fixed port. A listener with no `listen` takes the first free port of the Gateway's block, 7820 to
7839, at the first start, in that order, and the Gateway writes it to `config/server.yaml` once it listens on it. A Gateway started with no settings therefore listens
on 7820, 7821 and 7822, or the next free ports, and on the same three after every restart. A module (`apps`, `identity`, `store`, `views`, `feeds`, `mcp`)
has a `listen` of its own only when the administrator writes one (`apps.listen`, `rest.listen` for feeds); without it the module answers on the public listener.

The ports step of the start checks every address of every listener. A port that another program holds, or that Windows excludes, fails the step, and the failure file
names the port and the excluded ranges; the Gateway never moves a port it was given. A container sets the ports it publishes itself (`DBDATAKIT_Gateway__Listen=https://0.0.0.0:7821` in the image). The image does not set the public
listener, so inside the container it takes 7820 and is not published.

When a further Gateway joins an existing group, the join hands it the ports of the group's first instance: those of `public.listen`, `listen` and
`management.listen`, as the first instance reported them when it joined. The new instance keeps an address of its own and takes the scheme and the port
from the group:

- Its `config/server.yaml` names an address for the listener: that address, with the group's port.
- It names none: the listener's default address, `0.0.0.0` (`127.0.0.1` for the management API), with the group's port.

The instance writes the result to its `config/server.yaml` and its listeners start on it. If a port is taken on the new machine, the instance fails at
the ports step naming the port, listens on nothing, and never picks another. So every instance of a group listens on the same ports, and a client or a
balancer set for one instance is right for all of them.

A `listen` given on the command line or in an environment variable (the image's `DBDATAKIT_Gateway__Listen`) still wins over the file, so an instance
started with one keeps the port it was given. A port that an administrator changes by hand on the first instance reaches a new instance only after the
first instance joins again.

Example: the first Gateway of group `sales` takes 7820, 7821 and 7822 for its public, Flight SQL and management listeners (or the next free values) and
pins them, with Flight SQL on `https://10.0.0.5:7821`. A second instance joined to `sales` on another machine, with no settings, gets 7820, 7821 and 7822
from its join and writes `listen: https://0.0.0.0:7821`. It does not write `https://10.0.0.5:7821`, because that is the first machine's address and the
second machine cannot listen on it. If 7821 is held on the second machine, its `startup-failure-dbdatagateway.txt` names 7821 (and the program holding
it, if Windows says) and the instance does not start until the port is freed.

## Skipping the folder permissions check

`--skip-folder-permissions-check` (on `server run`, `service install` and `dbdatakit setup single-server`, which record it in the registered service's
settings), and the setting `startup.skip_folder_permissions_check: true`, let the folder step accept a folder whose owner or
access list is not the strict one: a test's temporary folder, a checkout, a test install. It is accepted everywhere, under a service manager too: the user
decides. It is always visible: a warning at every start, a line in `service check`, and the server marked on the console's posture page. Without a named
switch, strict checks get loosened in code "just for this case" and stay loose.

The setting goes in the server's own settings file: `settings.yaml` in the console's folder, `config/server.yaml` in the Gateway's, `server.yaml` in
the Runner's and the Workspace's.

```yaml
startup:
  skip_folder_permissions_check: true
```

Where a skipped check shows:

- At every start: a warning on standard error that names the switch and what the check would have found. A Windows service also writes it to the
  Application event log, as a warning.
- `service check`: the line `startup.folder-permissions` names the service and the switch (the registered command line, or the setting and its file).
- The console's Bindings page, under "Posture choices": a line names every server that skips the check, the console included. Each server reports it in
  the `loosened` list of its `/version` answer.

Without the switch, a folder that fails the check stops the start at the folder step. The failure file names the folder, its owner, the account, what is
wrong, and the flag and the setting.

Example: a developer starts a Runner on a folder every account can write to.

```
$ dbdatarunner server run --repo ~/work/runner-a
The start failed at the folder step: The folder '/home/ada/work/runner-a' is owned by ada, and the service runs as ada. Every account on the machine
can write to it (mode 0777). `dbdatakit setup single-server` sets the folder up. To start on the folder as it is (a test's folder, a checkout), give
`server run` --skip-folder-permissions-check or set startup.skip_folder_permissions_check: true in /home/ada/work/runner-a/server.yaml. The full reason
is in /home/ada/work/runner-a/logs/startup-failure-dbdatarunner.txt.

$ dbdatarunner server run --repo ~/work/runner-a --skip-folder-permissions-check
Warning: dbdatarunner skips the folder permissions check (--skip-folder-permissions-check): the data folder '/home/ada/work/runner-a' is used whatever
its owner and access list. The check would refuse it: Every account on the machine can write to it (mode 0777).
Serving on https://127.0.0.1:7840 ...
```

## Pairing

The console is the only pairing host and the root of trust; every service uses one pairing library. The console generates, on its command line or in
the web console, a command for the administrator to run on the remote machine:

```
dbdatarunner pair --host https://console:7800 --token <single-use> --console-thumbprint sha256:<hex>
```

The token is 128 random bits, shown once when it is made, single-use, short-lived (15 minutes by default; `--minutes` sets up to 24 hours) and scoped to one product, a role ceiling and the group it joins; the thumbprint makes the
first connection trusted. `pair` only records the join settings and (re)starts the service; the service joins at start as its own account, sends its
address and certificate fingerprint, and the console calls back once to confirm and pins it. A server calls the console once, to join; every other call is
the console calling the server. Users never copy codes between services except by running the generated command. `--token -` reads the token from
standard input for whoever prefers that. All automation is a subcommand of our programs, never an OS-specific script.

```
console (CLI or Add a server screen)       remote machine (administrator)                 the service, as itself
 invite: product, role, group  ─────►  dbdatarunner pair --host ... --token ... --console-thumbprint ...
   keeps a verifier made from the token     writes join-request.yaml, (re)starts ─────►  startup step "join":
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

**Join tokens are rows of the console's state**: a table of the `trust` module, added by its own migration, holding a verifier made from the token, its
product, role ceiling and group, who made it and when it ends. The verifier is the public half of a key derived from the token. It checks a join's
signature and cannot make one, so a copy of the console's state is not enough to join a server. The console's Add a server screen lists the tokens still waiting, never their values, and
cancels them.

**Groups.** The instances of a Gateway group or a Runner group share one state database, and with it one instance id. The token names the group
(`dbdatakit setup invite gateway --group sales`). The console keeps one key per group in its own secret store and hands it over in the join's answer: it
makes the key at the first instance's join, and every further instance that joins with a token for the group gets the same key, so nobody copies a key
by hand. The instance keeps the key in its own secret store. A key that does not open what the group's state already holds is refused, naming the
secret, and the join step fails. A Gateway that starts with a join request waiting makes no key of its own until the join has given it the group's.

A token for a named group is refused for a server whose state is another database than the group's (it would be a group of its own), and for an
instance the console already knows under another group. The joining server is told why:

```
The join token was made for group night, and this server is not an instance of it: the instances of a group share one state database, and this one has
its own. Point this server's state at the group's database, or make a command for a group of its own on the console.
```

Runner and Gateway groups never pair with each other directly. Linking a Gateway group with a Runner group is a console action (`docs/architecture.md`,
"What runs where"); a new instance of a linked group gets the link with its join.

**Networks where a server cannot reach the console.** The console can start the pairing instead. It still generates the command for the remote machine,
code included; it then either takes the remote's address up front, or waits for the user to run the command there and enter the address the command
prints. The console then connects to the server and pins it, with the same token scope and single use. Example: a Runner in a network that the console
can reach but that cannot reach the console's 7800; the administrator runs the generated command on it, types the address it prints into the console's
Add a server screen, and the Runner is listed ready.

## The console's publisher key

The console signs each version it deploys to a Runner or a Gateway with a publisher key of its own. Every Runner and Gateway group the console
manages trusts that key for each kind of definition the group takes, so a version the console signs verifies there when the group requires
signatures. Command lines do not publish to a Runner or a Gateway: the console deploys.

The console adds its key to each group itself:

- A server joins: the console adds the key to the server's group a few seconds after the join, once the server holds the console as a client.
- The console starts: it checks every Runner and Gateway it manages and adds the key where the group does not list it yet. A group that joined, or
  was paired by code, before the console had a publisher key gets it this way.
- A server does not answer: the console tries again later, waiting longer each time, up to ten minutes between tries.

The console asks the group which kinds it takes (`GET /api/v1/trust`) and adds the key for each kind that does not list it
(`PUT /api/v1/trust/{kind}/keys`), labelled `dbdatakit console <console id>`. Adding a key needs the administrator role on the server, which a
join gives the console unless the token named a lower ceiling. The group's Publishers page (the Gateway's `/trust`, the Runner's Publishing card)
shows the key. A key an administrator revoked there stays revoked: the trust store keeps it revoked, and the console does not add it again. A server
that refuses the key is recorded in the console's audit as `server.trust`, and the console tries again later.

Example: Ana joins a Runner with the command the Add a server screen printed. A few seconds later the Runner's Publishing card lists the key
`dbdatakit console console-3f2a9c1e07b4` for `integration`, and a package the console deploys to that Runner is recorded as signed and verified.

These commands answer that the console deploys, and exit with code 2:

- `dbdatarunner package pair`;
- `dbdatasync package publish --to runner` with `--server` or `--code` (`--out` and `--check` still write the package or say what it holds);
- `dbdatagateway apps publish --to` (`apps publish --dir` still publishes into the Gateway whose data folder it is given).

## Setting up one machine, or inviting another

`dbdatakit setup single-server` registers, starts and joins every suite service installed on the machine, through the shared service library (a library
call, not a command line), each with a single-use join token in its settings:

1. It finds the suite's programs installed beside `dbdatakit` (the folders of the suite's table of services, the layout every package uses) and, for each,
   the service it should register.
2. For each, in start order: it registers the service if it is not registered (or registers it again when it points elsewhere); sets the top folder's
   owner and access list; sets the program folder's access list (Administrators and SYSTEM full control, everyone else read and execute, inheritance cut,
   so a user cannot plant a file beside a program a service runs); for a server the console does not list yet, mints a join token and writes the join request; starts the service.
3. It waits for each to be listening and joined, printing `registered · started · listening · joined` per service and, for a service that fails, the
   contents of its `startup-failure-<service>.txt`.
4. It ends with a summary: what is ready, what is not, the console's address, and the first administrator's setup link from `FIRST-RUN.txt`. Run again,
   it skips what is done: a service registered here, running and joined is left alone, so it picks up where it stopped.

`--skip-folder-permissions-check` given to `setup single-server` is passed on to the services it registers and recorded in their settings ("Skipping the
folder permissions check" above). `dbdatakit setup single-server --stop` stops every suite service on the machine (before `scoop update`, for example).

Example: on a fresh machine `dbdatakit setup single-server` prints four lines that reach `joined`, then the console's address `https://host:7800` and the
setup link; if the Runner's configured port is held, its line stops at `started`, the contents of `startup-failure-dbdatarunner.txt` follow, the summary
names the Runner as not ready, and a second run after the port is freed finishes it and leaves the other three alone.

`dbdatakit setup invite <gateway|runner|workspace> [--role <ceiling>] [--group <g>]` mints a join token and prints the pairing command for a remote
machine; the console's Add a server screen makes the same call.

```
$ dbdatakit setup invite runner --role operator --url https://console.example.com:7800
Run this on the runner's machine, as an administrator, before 2026-11-21 09:27:03Z. It works once:
    dbdatarunner pair --host https://console.example.com:7800 --token k7d2mq4x5vbn6r3ta2wq4zj7pa --console-thumbprint sha256:3f0c9a5e...
```

`--url` is the address the server's machine reaches the console at; without it the command names the console's `listen:` address, and a
loopback address works for a server on the console's machine only. `--minutes` sets how long the token works (15 by default, up to 1440). The
command reads the console's certificate and writes the token into the console's state, so it runs on the console's machine; it fails, saying so,
before the console's first start.

The console's **Add a server** screen (in the Servers group of the navigation, for administrators) makes the same token:

- **Make the command.** The administrator chooses the server (Gateway, Runner or Workspace), ticks the roles of the ceiling (admin is ticked to
  start with), names a group if the server joins one, sets how many minutes the command works (15 to start with) and checks the console's address.
  The address starts as the one the browser opened the console at; a server on another machine needs an address that machine reaches. The screen
  shows the command once, with a Copy button: the console keeps no copy of the token, and a command that names a loopback address carries a note
  that only a server on the console's machine reaches it.
- **Waiting to be used.** Every token made on the screen or with `setup invite` is listed until a server spends it or it ends: the server, the
  ceiling, the group, who made it and when it ends. Cancel ends a token, so its command joins nothing; the console's audit records who cancelled it
  (`server.invite.cancel`), as it records who made it (`server.invite`).
- **Joined.** Once the server has joined, its token leaves the list and the server is listed under All servers.

Example: Ana opens Add a server, chooses Runner, keeps admin and presses Make the command. She copies `dbdatarunner pair --host
https://console.example.com:7800 --token k7d2mq4x5vbn6r3ta2wq4zj7pa --console-thumbprint sha256:3f0c9a5e...` to the Runner's machine and runs it there
as an administrator. The Runner restarts and joins, its token leaves the list, and All servers shows the Runner as Ready.

When a join is refused, the server's start stops at the join step and its startup-failure file says what the console answered, for example
`The console at https://console.example.com:7800 answered: The join token was already used. Make a new command on the console (dbdatakit setup
invite).` The join request stays in the data folder, so the next `pair` command replaces it.

**Docker Compose** uses the same path, with the tokens passed through a shared volume rather than environment variables (a variable would need the token
before the console exists, so it would have to be a long-lived secret written in the compose file). The console's container lists its members in its
settings (`setup.members`), writes their join requests into the shared volume `join` at its first start, and each member's container consumes its own at
its start. A port in the compose file is configured, so it is never moved. The quickstart has no `instance pending` and `instance confirm` steps.

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

`service check` checks a service's registration, settings and folder permissions, and prints the startup-failure file of the last start; `health check`
asks whether a running service answers. A command that checks something is named `check`, as `cert check` and `config check` are.
`scripts/check_docs_commands.py` fails when a tracked file (a command, a type, a test, a doc or a plan) uses the earlier name of `service check`
again; CI runs it in `repo-checks.yml`.

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
| `server pair`, `instance add --code`, `instance pending`, `instance confirm`, `publisher pair --code`, `bundle init` | the program (exit 2, `RemovedCommands`) | the sentence naming the new command |

Example: an administrator upgrades a machine whose Gateway was registered as `dbdatagateway` from `%ProgramData%\DbDataKit\bin\<version>` and paired by
a code. `dbdatakit setup single-server` says it removed `dbdatagateway` and registered `DbDataGateway` on the same folder, registered it against the
program folder and removed the copy, and removed the old `instances.yaml` entry and joined the Gateway again; the Gateway's folder step then names any file
the old install's administrator made and says to move the folder aside and run `setup single-server` again.
