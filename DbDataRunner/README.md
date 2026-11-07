# DbDataRunner

The runner: it schedules and runs the tasks of published packages, each in a process of its own, with retries, time and memory limits, a run history, logs and the progress a task keeps between runs (plan 28). A package is a manifest (`package/1`: its tasks, schedules, inputs and limits) and its code; the first kind it runs is a compiled .NET project, started through the task host. This is the single-runner host (slice R10): the engine in `DbDataKit.Runner`, a management API a console pairs with, its own state database and its command line. Groups of runners that share a state, the publish API and the console screens are the slices that follow (plan 28a).

```
dbdatarunner server run [--repo <server directory>] [--listen https://127.0.0.1:8495] [--channels prod,dev]
dbdatarunner server pair --max-role <roles> [--repo <server directory>]
dbdatarunner server clients | unpair <client> [--repo <server directory>]
dbdatarunner package install <manifest.yaml> <directory> [--channel dev] [--repo <server directory>]   # a development path, see below
dbdatarunner tasks list [--repo <server directory>]
dbdatarunner tasks run <package>/<channel>/<task> [name=value ...] [--repo <server directory>]
dbdatarunner tasks pause <package>/<channel>/<task> [--repo <server directory>]
dbdatarunner tasks resume <package>/<channel>/<task> [--repo <server directory>]
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

## Packages: `package install` is a development path

`dbdatarunner package install <manifest.yaml> <directory> --channel dev` stands in for plan 30's publish API (slice R16): it reads a `package/1` manifest and a directory of files (the published output of a .NET project, for instance), checks the manifest with the manifest's own validator, stores the files and makes the version active on the channel in the runner's own state, and replaces that channel's task schedule with the manifest's tasks. Installing the same content again changes nothing; changed content is the next version, which the scheduler switches to between runs. It needs the server's directory, not the server: it can run before the first start. The launch assembly must be among the files.

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

`tasks run|pause|resume` do the same from the command line, on the same state, and are audited the same way; what `tasks run` queues, a running server starts within a few seconds.

## What the process does

At start it listens on the socket its tasks use, registers an instance in the `runner` module with what the machine can enforce, marks the runs a dead process left behind as interrupted, and starts the scheduler on the channels it serves. It renews its registration every 15 seconds. At stop (Ctrl+C, SIGTERM, the service manager's stop) it cancels the running tasks and gives them their grace period (30 seconds, then the process tree is killed), puts their work items back in the queue, marks the instance gone and closes the socket: no run is left running.

## In a service

`dbdatarunner service install` registers `server run` with Windows (as `NT SERVICE\DbDataRunner`, the low-privilege account every task then runs as) or prints a hardened systemd unit with `service unit`; its directory is then `%ProgramData%\DbDataRunner\server`. `dbdatarunner server pair --max-role admin` makes the code a console pairs with.
