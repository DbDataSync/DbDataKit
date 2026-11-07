# DbDataRunner

The runner: it schedules and runs the tasks of published packages, each in a process of its own, with retries, time and memory limits, a run history, logs and the progress a task keeps between runs (plan 28). A package is a manifest (`package/1`: its tasks, schedules, inputs and limits) and its code; the first kind it runs is a compiled .NET project, started through the task host. This is the single-runner host (slice R10): the engine in `DbDataKit.Runner`, a management API a console pairs with, its own state database and its command line. Groups of runners that share a state, the publish API and the console screens are the slices that follow (plan 28a).

```
dbdatarunner server run [--repo <server directory>] [--listen https://127.0.0.1:8495] [--channels prod,dev] [--group main]
dbdatarunner server pair --max-role <roles> [--repo <server directory>]
dbdatarunner server clients | unpair <client> [--repo <server directory>]
dbdatarunner package pair <https address> --code <code> [--name <label>] [--client-dir <directory>]   # on a publisher's machine: pair with the runner
dbdatarunner package publish <manifest.yaml> <directory> --server <name or https address> --channel <name> [--code <code>] [--stage] [--expected-version <n>] [--client-dir <directory>]
dbdatarunner package require-signatures [on|off] [--repo <server directory>]
dbdatarunner package install <manifest.yaml> <directory> [--channel dev] [--repo <server directory>]   # the development path, see below
dbdatarunner tasks list [--repo <server directory>]
dbdatarunner tasks run <package>/<channel>/<task> [name=value ...] [--repo <server directory>]
dbdatarunner tasks pause <package>/<channel>/<task> [--repo <server directory>]
dbdatarunner tasks resume <package>/<channel>/<task> [--repo <server directory>]
dbdatarunner label add <label> --instance <id|this> [--repo <server directory>] [--group <name>]
dbdatarunner label remove <label> --instance <id|this> [--repo <server directory>] [--group <name>]
dbdatarunner label list [--instance <id|this>] [--repo <server directory>] [--group <name>]
dbdatarunner runs list [--package <name>] [--channel <name>] [--task <name>] [--outcome <outcome>] [--limit <n>] [--repo <server directory>]
dbdatarunner state show [--offline] [--engine mssql|postgres] [--repo <server directory>]    # the state database: engine, namespaces, module versions, pending migrations; creates nothing
dbdatarunner state test [--engine mssql|postgres] [--repo <server directory>]                  # opens it read-only and checks rights, engine version and clashes; stops at the first failure with the fix
dbdatarunner state grants --login <database role or user> [--migrate] [--engine mssql|postgres] [--repo <server directory>]
dbdatarunner service install|uninstall|status|start|stop|restart|doctor|unit
dbdatarunner cert show|ensure|check|renew [--repo <server directory>]
dbdatarunner health check [--url <url>]
```

## The server's directory

`--repo` (or `DBDATARUNNER_SERVER_DIR`; by default a folder under the user's local application data) names one directory that holds everything the runner keeps:

| Path | What |
| --- | --- |
| `state.db` | the state database when it is SQLite (the default); see below for SQL Server and PostgreSQL |
| `tls.pfx`, `tls-pfx-password` | the server's self-signed certificate; a client pins its fingerprint when it pairs |
| `cache/` | the materialized package versions: each file once by hash, a read-only directory per version; a run reads it and never writes it |
| `scratch/` | one directory per run (its working and temp directory), removed after the run |
| `run/` | the socket the task processes talk to, in a directory only the runner's account can use (mode 0700) |

The task host, `dbdatakit-taskhost`, is published into `taskhost/` beside the program by the build and by `dotnet publish`, with its own dependencies (it runs on plain .NET, not on the ASP.NET framework the server uses). A server that cannot find it does not start, and says where it looked.

## State

SQLite in `state.db` by default, and on a server engine when the environment names one: `DBDATARUNNER_STATE_ENGINE` (`mssql` or `postgres`) and `DBDATARUNNER_STATE_CONNECTION` (the connection string; a `secret:` reference in it is looked up in `DBDATA_SECRET_*` variables, so the password need not be written down). Every verb reads the same two variables, so `tasks`, `runs` and `package` work on the state the server uses. `state show` and `state test` say what the runner would open and whether it can; `state grants` prints what a database login needs. The tables are the modules `runner` (instances, capabilities, the task schedule), `runs` (history and logs), `runner_progress`, `scheduling` (the work queue, leases, job state and pauses), `definitions` and `payloads` (the published versions and their files), `audit`, `notifications` and `trust` (the instance id, the paired clients and the pairing codes).

## Publishing packages (plan 30, slice R16)

A publisher is another program or a person's machine paired with the runner as a **Publisher**: the administrator makes a single-use code with `dbdatarunner server pair --max-role publisher`, and the publisher runs `dbdatarunner package pair https://<runner>:8495 --code <code>` (or gives `--code` to its first `package publish`). The certificate is pinned; the signing key is kept in the encrypted secret store of the client directory (`--client-dir`, `DBDATARUNNER_CLIENT_DIR`).

`dbdatarunner package publish <manifest.yaml> <directory> --server <name or address> --channel <name>` asks the runner for every file's hash (`HEAD /api/v1/artifacts/<sha256>`), uploads only the files it does not have (`PUT`, streamed, one file at a time), then publishes the version in one call. A second publish with one changed file sends one file. `--stage` stores the version without serving it: it is not scheduled or run until an administrator activates it. `--expected-version N` refuses the publish if the package has moved on since you looked (0 for a first publish).

A publisher owns the packages it creates (the first publisher of a name owns it, `OwnerScope` of plan 13 W12): it can publish, stage and bind only those. Another client's package, or one an administrator made, is refused with 403 and audited. A viewer cannot publish. Activate, switch, rollback and retire are an administrator's. What is refused by the kind's validator, with the reason: the manifest is not valid `package/1`, it names another package than the path, the launch assembly (or an executable's relative command) is not among the files, a file was never uploaded, or the package was built against a .NET, kit or runner-protocol contract this runner does not serve (the message names both and asks for a republish).

**Signatures (plan 30 section 6.2) are not built yet.** Every version is recorded as `unsigned` (in the audit and in the publish answer). The seam is `IVersionSignatureVerifier` (the default checks nothing); the administrator's setting `require_signatures` (`package require-signatures on`, or `PUT /api/v1/settings/require_signatures`) refuses a version that is not verified, naming the missing signature, so with the setting on and no verifier every publish is refused.

After a publish with `serve` (the default until staging activates by itself, slice R17), an activation, a switch or a rollback, the runner rebuilds the task schedule of that package and channel, so the scheduler runs the version the channel serves; a retire removes the schedule.

## Packages: `package install` is the development path

`dbdatarunner package install <manifest.yaml> <directory> --channel dev` is the same code in process, without HTTP or pairing: it reads a `package/1` manifest and a directory of files (the published output of a .NET project, for instance), checks the manifest with the manifest's own validator, stores the files and makes the version active on the channel in the runner's own state, and replaces that channel's task schedule with the manifest's tasks. Installing the same content again changes nothing; changed content is the next version, which the scheduler switches to between runs. It needs the server's directory, not the server: it can run before the first start. The launch assembly must be among the files.

The runner serves the channels `prod` and `dev` by default (`--channels`, or `DBDATARUNNER_CHANNELS`, to change that); a task on another channel is stored but not scheduled.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. A viewer reads; an operator (an administrator is one) starts, pauses and resumes tasks, and each of those is written to the audit (`runner.task.run`, `runner.task.pause`, `runner.task.resume`: who, through which client, which task), a refused attempt too.

| Call | What |
| --- | --- |
| `GET /api/v1/status` | the state engine, the channels served, what this machine reports (`memory_limit`, `cpu_limit`, `os`, `network`), this process's instance id and every task's status: next due, last run, what is running or waiting, whether it is paused |
| `GET /api/v1/runs?package=&channel=&task=&outcome=&limit=&before=` | the run history, newest first; `task` needs the package and the channel |
| `POST /api/v1/tasks/<package>/<channel>/<task>/run` with an optional `{"inputs": {"name": "value"}}` | queues a run now, apart from the schedule (202 and the run id; 409 when one is queued or running already) |
| `POST /api/v1/webhooks/<package>/<channel>/<source>` with the body as is | delivers a webhook to the tasks with a webhook trigger for the source: a client with the operator or publisher role (the serving layer's call), or a sender with no token and `X-Signature: sha256=<hmac of the body>` under the trigger's `secret`. 202 with the inbox items; 401 for a signature that does not hold (the body is kept, flagged, and no run takes it); 404 when no task listens. Audited without the body |
| `POST /api/v1/tasks/<package>/<channel>/<task>/pause`, `.../resume` | stops the task from being queued on its schedule, and lets it be |
| `HEAD`, `PUT /api/v1/artifacts/<sha256>` | is the file stored (publisher or viewer); upload it, streamed, refused when its hash is not the name or it is over the size cap (`DBDATARUNNER_MAX_ARTIFACT_BYTES`, 512 MiB by default) |
| `PUT /api/v1/definitions/integration/<name>` with `manifest`, `artifacts`, `channel`, `mode` (`stage`, the default, or `serve`), `expected_version`, `depends_on`, `note` | publish (publisher within its packages, or an administrator) |
| `POST /api/v1/definitions/integration/<name>/activate`, `/switch`, `/rollback`, `/retire` with `channel` and `expected_version` | the life cycle (administrator). A staged version is prepared by every runner and activates by itself when all live ones are ready and its progress migration loses nothing. `activate` with `confirm_losses: true` and the loss list echoed in `losses` activates one that would lose progress; with `anyway: true` (and a `reason`) one whose runners failed or have not reported (audited). An activation waits for the runs in flight (at most `DBDATARUNNER_ACTIVATION_MAX_WAIT_SECONDS`, 600 by default; 202 with the busy tasks when that runs out), then migrates the progress, moves the version and rebuilds the schedule in one transaction; `rollback` puts the progress back |
| `PUT /api/v1/bindings/<channel>/integration/<name>` with `slots` | binds the package's slots for a channel |
| `GET /api/v1/definitions/integration[/<name>]` | the versions, what each channel serves (active, previous, staged), bindings, what each instance reports, and the latest events (viewer) |
| `GET /api/v1/settings`, `PUT /api/v1/settings/require_signatures` | the publishing settings (the second is an administrator's) |

`tasks run|pause|resume` do the same from the command line, on the same state, and are audited the same way; what `tasks run` queues, a running server starts within a few seconds.

## What the process does

At start it listens on the socket its tasks use, registers an instance in the `runner` module with what the machine can enforce, marks the runs a dead process left behind as interrupted, and starts the scheduler on the channels it serves. It renews its registration every 30 seconds (the lease is 90 seconds; both are settings). At stop (Ctrl+C, SIGTERM, the service manager's stop) it cancels the running tasks and gives them their grace period (30 seconds, then the process tree is killed), puts their work items back in the queue, marks the instance gone and closes the socket: no run is left running.

## In a service

`dbdatarunner service install` registers `server run` with Windows (as `NT SERVICE\DbDataRunner`, the low-privilege account every task then runs as) or prints a hardened systemd unit with `service unit`; its directory is then `%ProgramData%\DbDataRunner\server`. `dbdatarunner server pair --max-role admin` makes the code a console pairs with.

## Groups, labels and routing (slices R11, R12)

Runners that share one SQL Server or PostgreSQL state database and one group name (`--group`, or `DBDATARUNNER_GROUP`; default `main`) are a **group**: any runner takes any task. SQLite is for one runner: a second server on the same SQLite state refuses to start and names the first (its instance id, host, start and last heartbeat). Every process has one row in the group's instances, renewed every 30 seconds. A row that stops being renewed lapses after the lease (90 seconds); the work items its runner held go to another runner then. A runner that comes back after its items were taken is told so in its log, cancels the local task and does not record its result: its late finish is refused. A crashed process's row is removed when the next process starts on that machine, or by retention.

`GET /api/v1/instances` and the `instances` list of `GET /api/v1/status` show the live instances: id, host, version, labels (each with who set it), capabilities and how many items each is running.

A **label** is `name` or `name:value`. The runner reports what it finds as read-only labels (`network:outside`, `os:linux`, `memory_limit:enforced|watched`); an administrator adds others with `label add`, for one instance (an id from `label list`, or `this` for the live runner on this machine; a restarted runner keeps its administrator's labels). A task lists the labels it needs in its manifest (`requires: [network:outside, gpu]`); a work item is claimed only by a runner that has every one. A task whose label no live runner has stays pending, and the `waiting_reason` of its entry in the status call (and `tasks list`) names the label; once a runner has the label it starts within a tick. A version whose required label no live runner has must not activate (`RoutingCheck.CanActivate`, used by staging in plan 30).
