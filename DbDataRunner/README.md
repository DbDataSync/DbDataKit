# DbDataRunner

The runner: it schedules and runs the tasks of published packages, each in a process of its own, with retries, time and memory limits, a run history, logs and the progress a task keeps between runs. A package is a manifest (`package/1`: its tasks, schedules, inputs and limits) and its code; the first kind it runs is a compiled .NET project, started through the task host. This is the runner's host: the engine is the library `DbDataKit.Runner` (its README has the rules the engine keeps), the host adds a management API a console pairs with, its own state database, the publish API and its command line. Groups of runners share one state database; the console hosts a copy of the engine too (below).

```
dbdatarunner server run [--repo <server directory>] [--listen https://127.0.0.1:8495] [--channels prod,dev] [--group main]
dbdatarunner server pair --max-role <roles> [--repo <server directory>]
dbdatarunner server clients | unpair <client> [--repo <server directory>]
dbdatarunner package pair <https address> --code <code> [--name <label>] [--client-dir <directory>]   # on a publisher's machine: pair with the runner
dbdatarunner package publish <manifest.yaml> <directory> --server <name or https address> --channel <name> [--code <code>] [--stage] [--expected-version <n>] [--client-dir <directory>]
dbdatarunner package require-signatures [on|off] [--repo <server directory>]
dbdatarunner package require-sandbox-run [on|off] [--repo <server directory>]
dbdatarunner package allow-production-binding <package> <slot> <channel> [--confirm] [--revoke] [--note <text>] [--repo <server directory>]
dbdatarunner package install <manifest.yaml> <directory> [--channel dev] [--repo <server directory>]   # the development path, see below
dbdatarunner package build <project directory> --out <directory> [--source <feed>]... [--cache <directory>] [--offline] [--locked] [--no-lock-write]   # an integration project into package.yaml and files/, csx without the SDK
dbdatarunner package test <project directory> [--source <feed>]... [--cache <directory>] [--offline] [--locked] [--no-lock-write]   # build, then run tests/*.csx against the package with RunSimulator
dbdatarunner tasks list [--repo <server directory>]
dbdatarunner tasks run <package>/<channel>/<task> [name=value ...] [--dry-run] [--repo <server directory>]
dbdatarunner tasks pause <package>/<channel>/<task> [--repo <server directory>]
dbdatarunner tasks resume <package>/<channel>/<task> [--repo <server directory>]
dbdatarunner label add <label> --instance <id|this> [--repo <server directory>] [--group <name>]
dbdatarunner label remove <label> --instance <id|this> [--repo <server directory>] [--group <name>]
dbdatarunner label list [--instance <id|this>] [--repo <server directory>] [--group <name>]
dbdatarunner lineage graph [--kind <kind>] [--repo <server directory>] [--group <name>]   # the deployment lineage of the group (plan 28c E24): packages, versions, channels, runners, runs; the directory and group are the other local verbs'
dbdatarunner lineage impact <node> [--depth <n>] [--repo <server directory>] [--group <name>]
dbdatarunner lineage provenance <node> [--depth <n>] [--repo <server directory>] [--group <name>]
dbdatarunner lineage where <definition> [--channel <channel>] [--repo <server directory>] [--group <name>]   # where is this deployed: channels, active and previous version, who activated and when, the runners ready or active
dbdatarunner lineage on <runner> [--repo <server directory>] [--group <name>]   # what is deployed on a runner (an instance id or a host)
dbdatarunner gateway add <name> --url <https address> --code <pairing code> [--repo <server directory>]   # pair this runner with a serving group (Gateway) as a server client: it then tells the group when a task wrote a dataset (plan 24a UC13); the Gateway's administrator makes the code with `dbdatagateway server pair --max-role publisher`
dbdatarunner gateway list [--repo <server directory>]   # the serving groups this runner is paired with (`gateways.json` in the directory)
dbdatarunner gateway remove <name> [--repo <server directory>]
dbdatarunner lineage runs <definition> <version> [--repo <server directory>] [--group <name>]   # the runs that named a version
dbdatarunner runs list [--package <name>] [--channel <name>] [--task <name>] [--outcome <outcome>] [--limit <n>] [--repo <server directory>]
dbdatarunner state show [--offline] [--engine mssql|postgres] [--repo <server directory>]    # the state database: engine, namespaces, module versions, pending migrations; creates nothing
dbdatarunner state test [--engine mssql|postgres] [--repo <server directory>]                  # opens it read-only and checks rights, engine version and clashes; stops at the first failure with the fix
dbdatarunner state grants --login <database role or user> [--migrate] [--engine mssql|postgres] [--repo <server directory>]
dbdatarunner state init|export|import|verify|move ...   # the shared verbs over the `state:` section of `server.yaml` (plan 29b C07); `move` also takes `--to-sqlite <file>`
dbdatarunner service install|uninstall|status|start|stop|restart|doctor|unit
dbdatarunner cert show|ensure|check|renew [--repo <server directory>]
dbdatarunner health check [--url <url>]
```

## The server's directory

`--repo` (or `DBDATAKIT_DATA_RUNNER`, or the folder `runner` under `DBDATAKIT_DATA`; by default a folder under the user's local application data) names one directory that holds everything the runner keeps:

| Path | What |
| --- | --- |
| `state.db` | the state database when it is SQLite (the default); see below for SQL Server and PostgreSQL |
| `tls.pfx`, `tls-pfx-password` | the server's self-signed certificate; a client pins its fingerprint when it pairs |
| `cache/` | the materialized package versions: each file once by hash, a read-only directory per version; a run reads it and never writes it |
| `scratch/` | one directory per run (its working and temp directory), removed after the run |
| `run/` | the socket the task processes talk to, in a directory only the runner's account can use (mode 0700) |

The task host, `dbdatakit-taskhost`, is published into `taskhost/` beside the program by the build and by `dotnet publish`, with its own dependencies (it runs on plain .NET, not on the ASP.NET framework the server uses). A server that cannot find it does not start, and says where it looked.

## State

SQLite in `state.db` by default, and on a server engine when the shared `state:` section of `server.yaml` in the server's directory names one (read strictly, never created; `state init` and `state move` write it), or when the environment does (the variables win over the file, `state show` says where each setting came from): `DBDATAKIT_STATE_RUNNER_ENGINE` (`mssql` or `postgres`) and `DBDATAKIT_STATE_RUNNER_CONNECTION` (the connection string; a `secret:` reference in it is looked up in `DBDATAKIT_SECRET_*` variables, so the password need not be written down). Every verb reads the same two variables, so `tasks`, `runs` and `package` work on the state the server uses. `state show` and `state test` say what the runner would open and whether it can; `state grants` prints what a database login needs. The management API has the state endpoints every server has (plan 29b C05, `DbDataKit.State.Api`): `GET /api/v1/state`, the maintenance mode (the runner then schedules and claims nothing new, finishes what runs and reports `idle`) and the move of its own state, which a paired console starts from Servers > State (PostgreSQL, SQL Server, or a SQLite file on the runner's machine). The tables are the modules `runner` (instances, capabilities, the task schedule), `runs` (history and logs), `runner_progress`, `scheduling` (the work queue, leases, job state and pauses), `definitions` and `payloads` (the published versions and their files), `audit`, `notifications` and `trust` (the instance id, the paired clients and the pairing codes).

## Publishing packages

A publisher is another program or a person's machine paired with the runner as a **Publisher**: the administrator makes a single-use code with `dbdatarunner server pair --max-role publisher`, and the publisher runs `dbdatarunner package pair https://<runner>:8495 --code <code>` (or gives `--code` to its first `package publish`). The certificate is pinned; the signing key is kept in the encrypted secret store of the client directory (`--client-dir`, `DBDATAKIT_RUNNER_CLIENT_DIR`).

`dbdatarunner package publish <manifest.yaml> <directory> --server <name or address> --channel <name>` asks the runner for every file's hash (`HEAD /api/v1/artifacts/<sha256>`), uploads only the files it does not have (`PUT`, streamed, one file at a time), then publishes the version in one call. A second publish with one changed file sends one file. `--stage` stores the version without serving it: it is not scheduled or run until an administrator activates it. `--expected-version N` refuses the publish if the package has moved on since you looked (0 for a first publish).

A publisher owns the packages it creates (the first publisher of a name owns it, `OwnerScope` of plan 13 W12): it can publish, stage and bind only those. Another client's package, or one an administrator made, is refused with 403 and audited. A viewer cannot publish. Activate, switch, rollback and retire are an administrator's. What is refused by the kind's validator, with the reason: the manifest is not valid `package/1`, it names another package than the path, the launch assembly (or an executable's relative command) is not among the files, a file was never uploaded, or the package was built against a .NET, kit or runner-protocol contract this runner does not serve (the message names both and asks for a republish), or the manifest uses a newer key of `package/1` this runner does not read (`launch.runtime`, `runtime`: the message names the key). A definition package that names its runtime (`launch.runtime`) is staged only when the channel has a runtime of that name active inside the range (plan 28c E04).

**Signatures (plan 30 section 6.2) are not built yet.** Every version is recorded as `unsigned` (in the audit and in the publish answer). The seam is `IVersionSignatureVerifier` (the default checks nothing); the administrator's setting `require_signatures` (`package require-signatures on`, or `PUT /api/v1/settings/require_signatures`) refuses a version that is not verified, naming the missing signature, so with the setting on and no verifier every publish is refused.

A runtime package (plan 28c E04) is run from a read-only view shared by the runs that use it; the view is made of links, except for a runtime that a host program starts (`dotnet {runtime}/bin/Engine.dll`, as Sync's engine is), which gets copies, because the host looks for its runtime configuration beside the object a link points at.

After a publish with `serve` (the default of a published version is `stage`; `package install` serves), an activation, a switch or a rollback, the runner rebuilds the task schedule of that package and channel, so the scheduler runs the version the channel serves; a retire removes the schedule.

## Packages: `package install` is the development path

`dbdatarunner package install <manifest.yaml> <directory> --channel dev` is the same code in process, without HTTP or pairing: it reads a `package/1` manifest and a directory of files (the published output of a .NET project, for instance), checks the manifest with the manifest's own validator, stores the files and makes the version active on the channel in the runner's own state, and replaces that channel's task schedule with the manifest's tasks. Installing the same content again changes nothing; changed content is the next version, which the scheduler switches to between runs. It needs the server's directory, not the server: it can run before the first start. The launch assembly must be among the files.

The runner serves the channels `prod` and `dev` by default (`--channels`, or `DBDATAKIT_RUNNER_CHANNELS`, to change that); a task on another channel is stored but not scheduled.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. A viewer reads; an operator (an administrator is one) starts, pauses and resumes tasks, and each of those is written to the audit (`runner.task.run`, `runner.task.pause`, `runner.task.resume`: who, through which client, which task), a refused attempt too.

| Call | What |
| --- | --- |
| `GET /api/v1/status` | the state engine, the channels served, what this machine reports (`memory_limit`, `cpu_limit`, `os`, `network`), this process's instance id and every task's status: next due, last run, what is running or waiting, whether it is paused |
| `GET /api/v1/instances[?all=true]` | the group's live instances (host, version, labels with their source, capabilities, items running); `all=true` adds the ones that are gone or silent past their lease, marked `live: false`, which is how the console shows a runner that left |
| `GET /api/v1/instances/<id>/labels`, `PUT /api/v1/instances/<id>/labels` with `labels` | the labels of an instance with who set each (viewer); the second sets the administrator's labels of a running instance to exactly the list (an administrator; each change audited as `runner.label.add` or `remove`; a label that is not one, one the machine reports, more than 32 or a gone instance is refused) |
| `GET /api/v1/lineage` | the deployment graph of the group in the shared lineage wire form (plan 18, plan 28c E24): package, version, channel, runner instance and runtime nodes, the version each instance holds in each channel with its state, and the newest runs joined to the version they named; the console joins it with the other servers and answers "where is this deployed" from it (viewer) |
| `GET /api/v1/tracking`, `POST /api/v1/tracking/divergences/<id>/resolve` with `{"resolution": "archive"\|"reset"}` | the runner's own copy of Build's tracking tables (plan 28c E16, E18): per identity (`buildtracking:<engine>://<server>/<database>.<schema>.tracking@<connection>`) and stream the checkpoint and row count, and every divergence, open or resolved, with its common point (viewer); the second resolves an open divergence of that copy with the same rules as the Build server's: `archive` keeps the copy's later rows apart, `reset` deletes them, and the copy then follows the target again from the common point (an administrator; audited as `runner.tracking.resolve`, a refusal too; 404 for no such divergence, 409 for one already closed). A project turns the copy off with `tracking: {runner_copy: false}` in its `dbdatabuild.yml`: the follower step after a refresh and the `catch-up` task then copy nothing and say so in the run log |
| `GET /api/v1/runs?package=&channel=&task=&outcome=&limit=&before=` | the run history, newest first; `task` needs the package and the channel |
| `GET /api/v1/runs/<id>`, `GET /api/v1/runs/<id>/logs?after=&limit=` | one run, and its log in the order the lines were written, a page at a time by line id (`after` is the id of the last line seen; at most 1000 a page); the lines of a run that is still going are there while it goes. A page is the next `limit` lines (default 200) with an id greater than `after`, in id order: never more than `limit`, and a line written later always has a higher id, so a client that follows with the last id it saw gets each line once. A run is finished only after its log is complete (the runner's note on the process's exit is the last line), so a finished run's log does not grow. 404 for a run the runner does not have (viewer; plan 28c E13: what Sync's live view and run log read for a replication the runner owns) |
| `GET /api/v1/tasks/<package>/<channel>/<task>/steps/<name>?limit=&before=&key=` | the completed steps of that name across the task's runs, newest first, each with its key, when it started and completed and its result as the JSON the step stored (viewer; plan 28c E13: Sync's drains keep a `pass-record` per pass and the `change-checks` of the polling gate there) |
| `PUT /api/v1/secrets/<name>` (body `{"value": "..."}`), `GET /api/v1/secrets[?prefix=]`, `DELETE /api/v1/secrets/<name>` | store a login under a `dbdatakit:<service>:<kind>:<name>[:<part>]` name in the runner's encrypted store, list the names (never values), remove one (admin; audited with the name and never the value, a refusal too; 503 saying so when the runner cannot keep a secret; plan 28c E20d) |
| `GET /api/v1/tasks/<package>/<channel>/<task>/steps/<name>/payloads/<id>` | the file a completed step of that name kept as its streamed result (the payload the step row owns; 404 when it is not a result of that task's step of that name, is not complete or the retention removed it; viewer; plan 28c E06e: Sync reads a verification result's parquet this way) |
| `POST /api/v1/tasks/<package>/<channel>/<task>/run` with an optional `{"inputs": {"name": "value"}}` | queues a run now, apart from the schedule (202 and the run id; 409 when one is queued or running already) |
| `POST /api/v1/webhooks/<package>/<channel>/<source>` with the body as is | delivers a webhook to the tasks with a webhook trigger for the source: a client with the operator or publisher role (the serving layer's call), or a sender with no token and `X-Signature: sha256=<hmac of the body>` under the trigger's `secret`. 202 with the inbox items; 401 for a signature that does not hold (the body is kept, flagged, and no run takes it); 404 when no task listens. Audited without the body |
| `POST /api/v1/tasks/<package>/<channel>/<task>/pause`, `.../resume` | stops the task from being queued on its schedule, and lets it be |
| `HEAD`, `PUT /api/v1/artifacts/<sha256>` | is the file stored (publisher or viewer); upload it, streamed, refused when its hash is not the name or it is over the size cap (`DBDATAKIT_RUNNER_MAX_ARTIFACT_BYTES`, 512 MiB by default) |
| `PUT /api/v1/definitions/integration/<name>` with `manifest`, `artifacts`, `channel`, `mode` (`stage`, the default, or `serve`), `expected_version`, `depends_on`, `note` | publish (publisher within its packages, or an administrator) |
| `POST /api/v1/definitions/integration/<name>/activate`, `/switch`, `/rollback`, `/retire` with `channel` and `expected_version` | the life cycle (administrator). A staged version is prepared by every runner and activates by itself when all live ones are ready and its progress migration loses nothing. `activate` with `confirm_losses: true` and the loss list echoed in `losses` activates one that would lose progress; with `anyway: true` (and a `reason`) one whose runners failed or have not reported (audited). An activation waits for the runs in flight (at most `DBDATAKIT_RUNNER_ACTIVATION_MAX_WAIT_SECONDS`, 600 by default; 202 with the busy tasks when that runs out), then migrates the progress, moves the version and rebuilds the schedule in one transaction; `rollback` puts the progress back |
| `PUT /api/v1/bindings/<channel>/integration/<name>` with `slots`, and `confirm`, `note` | binds the package's slots for a channel. A non-production channel bound to the connection production uses for the same slot is refused (409, `needsConfirmation`: scope, slot, channel, connection, effect) until `confirm` names each scope; the choice is recorded in the runner's own `posture_choice` (`production_binding_on_non_production`, scope `bindings/runner/<package>/<slot>/<channel>`) and taken back when the binding moves (plan 27a IX17) |
| `GET /api/v1/connection-copies`, `PUT /api/v1/connection-copies/<name>` with `{"definition": "<connection yaml>"}`, `DELETE ...` | the connections the console hands this runner (plan 27a IX23, `docs/channel-bindings.md`): the definition with its secret **references**, never a value, kept in the runner's own `connection_copy` table with a version (the hash of the text, which the console compares with its own file), audited by name and version. The list says which package slots use each copy, whether a file of the runner's own `connections/` folder shadows it, and for **each reference whether it resolves on this runner** (administrator to put or delete, viewer to read). A slot bound to the name resolves from the copy (an `http` copy gives the sign-in method with the secrets resolved here) |
| `GET /api/v1/definitions/integration[/<name>]` | the versions, what each channel serves (active, previous, staged), bindings, the connection `slots` each channel's served and staged manifests declare, what each instance reports, and the latest events (viewer) |
| `GET /api/v1/settings`, `PUT /api/v1/settings/require_signatures`, `PUT /api/v1/settings/require_sandbox_run` | the publishing settings (the others are an administrator's) |

`tasks run|pause|resume` do the same from the command line, on the same state, and are audited the same way; what `tasks run` queues, a running server starts within a few seconds.

**Dry run, sandbox run, and production's connection in another channel (plan 27a IX16, IX17).**

* `tasks run <task> --dry-run` (or `{"dry_run": true}` on `POST .../run`) queues a **dry run**: the task reads what it reads, and nothing it keeps changes. The reserved input `dbdatakit.dry_run` carries the word to the task; the kit takes it out before the job's input is bound and gives the job `Run.DryRun`. The kit's parts behave: progress is not saved (`SaveProgressAsync` does nothing), the file ledger is not written, an inbox item is not taken, a refused record is listed instead of kept; `OutsideHttpOptions.DryRun` refuses a request that is not a read and lists it, and a `WriteGate` with `DryRun` runs no statement. What it would have done is in the run's log as "Dry run, would: ...", under a first line that says it is a dry run; the audit of `runner.task.run` says `dry_run`. A dry run does not start the tasks that follow it (`after` triggers set its end aside). **A job that writes through code of its own is not guarded**: the guarantee is for the kit's clients and landing helpers.
* `package require-sandbox-run on` (`PUT settings/require_sandbox_run`, an administrator's, audited as `runner.setting.set`) makes `activate` and `switch` on `prod` refuse a version that has had no **sandbox run**: a run of that version that succeeded in a channel other than `prod`, where the slots are bound to that channel's own connections, and was not a dry run. It is read from the run history; nothing else is kept.
* A task in a channel other than `prod` is refused the connection `prod` binds the same slot to (at the start of the run, so a hand-made binding is held to it too): the slot's connection would send or write on production's far side. A read-only connection is exempt. An administrator's recorded choice allows it (`package allow-production-binding <package> <slot> <channel> --confirm`: the posture setting `production_binding_on_non_production`, scope `bindings/runner/<package>/<slot>/<channel>`, who and when in the audit; `--revoke` takes it back). A slot with no binding in the channel is refused as before: nothing is inherited from `prod`.

## What a package can be

A **package** is a manifest (`package/1`, see `DbDataKit.Runner.Abstractions`) and its code, versioned and published per channel; the runner switches to a new version atomically and keeps the last good one. The runner starts two things: a **.NET assembly** through the task host (a compiled project, or a `.csx` script published compiled: the runner never compiles) and an **executable** (any language; it speaks the runner protocol, `DbDataKit.Runner.Protocol`, or only exits, and an executable inside a package names an interpreter). A **runtime package** (`runtime: true`) is an engine published once that definition packages name in `launch.runtime`; Sync's and Build's engines run this way (plan 28c). A notebook kind (a .NET notebook over a project, run headless) is not built.

## Logins of a Sync replication set

A replication set (plan 28c) runs with the **run** login of each of its connections and never sees the read login Sync's own screens use (Sync's docs, "Two logins: read and run"). The run login is sent with `dbdatasync secret send --to runner <connection>` (plan 28c E20d: an admin pairing; the runner keeps it in its encrypted store `secrets/` and the reference `secret:dbdatakit:sync:connection:<name>:run` resolves to it, after the environment and the credentials directory) or put on the runner's machine by hand, for each connection of the set:

1. `<runner directory>/connections/<name>.yaml`, a `connection/1` file named like the Sync connection, with `auth: {mode: sql, user: <run user>, password_secret: env:<NAME>_RUN_PASSWORD}`; `secret:dbdatakit:sync:connection:<name>:run` works too (the environment variable
   `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_<NAME>_RUN`, or a file of that name in `CREDENTIALS_DIRECTORY`).
2. The password in the runner service's environment (systemd `LoadCredential=` or an `EnvironmentFile=` only the service account reads; the Windows service's environment): never in a file of the package or on a command line. An environment variable needs a restart of the runner;
   a file in the credentials directory is read at each run.
3. The slot bound to that connection in the channel (`PUT /api/v1/bindings/<channel>/integration/<name>`, or Sync's Runner screen).

The runner hands the engine the value as `DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_<NAME>` and `..._<NAME>_RUN` (it cannot tell which of the two a connection reads), registers it with the log redactor and writes it nowhere else. A login that is missing fails the run naming the slot, the
connection and the reference.

## The security posture

The suite gives simple options, guardrails and secure defaults, and **administrators decide**: it does not refuse a configuration because it could be a security concern, it makes the choice explicit, and every loosened default is a recorded choice (who, when, what changes). One kind of runner: any runner can run any task, and an administrator may label runners and route packages by label (`requires`). The environment is the administrator's: the runner reports what the machine can enforce and reach (`os`, `network`, `memory_limit`, `cpu_limit`) and enforces no network policy of its own. The defaults: every task in its own process, under the runner's low-privilege service account, with time and memory limits and packages only from published, hash-verified artifacts. A package that asks for more (run in process, longer limits, a more privileged account, a recompile on the instance) gets it only through a posture choice. A runner's own write logins are acceptable by design: Build's write login leaving Build is no concern, as DbDataStore's data write login already lives outside its server.

An administrator reads and records the choices from the console's Packages screen (the **Security posture** card) or the API: `GET /api/v1/posture-choices` gives every default that can be loosened with what it is today and what loosening it changes, and the choices recorded so far (who, when, for which package or `*`); `PUT /api/v1/posture-choices` with `{"setting", "scope", "confirm": true, "note"}` records one (refused with the effect named when `confirm` is not set) and with `{"setting", "scope", "revoke": true}` takes it back. Both are audited (`runner.posture.choose`, `runner.posture.revoke`). The choice of a production connection in a non-production channel is made where the binding is made, not here. (The embedded runner's `/api/embedded-runner/posture` is the console's own report of whether production was accepted: another thing.)

## What an operator answers

What the kit leaves to a person is answered on the runner's task screens and over the API, as an operator, every act audited under the person's name (`RunnerControl`):

- **Steps in doubt.** `GET /api/v1/tasks/{package}/{channel}/{task}/in-doubt` lists the steps an earlier attempt began and never finished. `POST .../steps/{name}/resolve` with `{"chain", "key", "happened", "result", "retry"}` says whether the step happened (and its result as JSON, which the next attempt reuses) or not (the next attempt runs it), and with `retry` queues a run of the task to go on (`runner.step.resolve`; 409 when the step is not waiting).
- **Dead letters.** `GET .../dead-letters[?status=open|replayed|discarded]` lists the records an outside system refused. `POST .../dead-letters/{id}/replay` makes the body an inbox item of the same task (the letter's id is the item's id, so replaying twice is one item) and queues a run to take it (`runner.deadletter.replay`); `POST .../dead-letters/{id}/discard` gives it up (`runner.deadletter.discard`).
- **Checkpoints.** `GET .../checkpoints`, `.../checkpoints/{name}/history` and `POST .../checkpoints/{name}/rewind` (`runner.checkpoint.rewind`).
- **Units of a run.** `GET /api/v1/runs/{id}/units` (the table: state, rows, duration, error, with the totals) and `/steps`, read from the attempt chain the run's work item carries; a run that a retry replaced (the item has a newer run id) answers 404.

## The console's embedded runner

The console hosts a copy of the engine (`RunnerHost`) as a group of one instance named `console`, with its state in the console's state database (scope `console`); its screens are the runner screens, reached at `/relay/console-runner/api/v1/...` and `/api/embedded-runner/...`. It exists for evaluation runs (plan 27), system tasks of the console and the install (retention of the console's own state; `state-retention` is a task of it) and development packages, so a single-machine install has a working runner with nothing more to install.

* It accepts by default **only non-production channels and the console's own task kinds**. An administrator can let it run production packages as an explicit, recorded choice (`AcceptProductionOnConsoleRunner`), and the console then warns that production schedules stop when the console does.
* The same isolation as any runner: a process per task, time and memory limits, and a low-privilege account that is not the console's own. Because no account switch exists yet, `account_separate` is always false and `console_runner.require_separate_account: true` refuses every package of its own (system tasks only).
* A cap on what it runs at once (`max_concurrent`), so a heavy run cannot slow the console's screens; stopping the console cancels the running tasks within the grace period and leaves no run running.
* Its version is the console's, so a package it accepts must be compatible with the console's release (N and N-1). A product's own housekeeping stays with the product: retention of a serving group's or a server's state runs in that server, so it keeps running while the console is down.
* `evaluation` is a placeholder until plan 27 builds evaluation runs.

## What the process does

At start it listens on the socket its tasks use, registers an instance in the `runner` module with what the machine can enforce, marks the runs a dead process left behind as interrupted, and starts the scheduler on the channels it serves. It renews its registration every 30 seconds (the lease is 90 seconds; both are settings). At stop (Ctrl+C, SIGTERM, the service manager's stop) it cancels the running tasks and gives them their grace period (30 seconds, then the process tree is killed), puts their work items back in the queue, marks the instance gone and closes the socket: no run is left running.

## In a service

`dbdatarunner service install` registers `server run` with Windows (as `NT SERVICE\DbDataRunner`, the low-privilege account every task then runs as) or prints a hardened systemd unit with `service unit`; its directory is then `%ProgramData%\DbDataKit\runner` (`/var/lib/dbdatakit/runner` for the Linux unit). `dbdatarunner server pair --max-role admin` makes the code a console pairs with.

## Groups, labels and routing

Runners that share one SQL Server or PostgreSQL state database and one group name (`--group`, or `DBDATAKIT_RUNNER_GROUP`; default `main`) are a **group**: any runner takes any task. SQLite is for one runner: a second server on the same SQLite state refuses to start and names the first (its instance id, host, start and last heartbeat). Every process has one row in the group's instances, renewed every 30 seconds. A row that stops being renewed lapses after the lease (90 seconds); the work items its runner held go to another runner then. A runner that comes back after its items were taken is told so in its log, cancels the local task and does not record its result: its late finish is refused. A crashed process's row is removed when the next process starts on that machine, or by retention.

`GET /api/v1/instances` and the `instances` list of `GET /api/v1/status` show the live instances: id, host, version, labels (each with who set it), capabilities and how many items each is running.

A **label** is `name` or `name:value`. The runner reports what it finds as read-only labels (`network:outside`, `os:linux`, `memory_limit:enforced|watched`); an administrator adds others with `label add`, for one instance (an id from `label list`, or `this` for the live runner on this machine; a restarted runner keeps its administrator's labels). A task lists the labels it needs in its manifest (`requires: [network:outside, gpu]`); a work item is claimed only by a runner that has every one. A task whose label no live runner has stays pending, and the `waiting_reason` of its entry in the status call (and `tasks list`) names the label; once a runner has the label it starts within a tick. A version whose required label no live runner has must not activate (`RoutingCheck.CanActivate`, used by staging in plan 30).

## Integrations with outside systems: what is built and what is not

An **integration** is a package of the kind `integration` (the kind every package here is published as): a manifest of tasks, schedules, triggers and the connection slots each needs, with ordinary code. The runner is the whole run-time side of plan 27's DbDataIntegration, and it is built and tested as described above. What the plan adds around it for talking to **systems that are not databases** is **not built**, and [plan 27a](../docs/plans/27a-knowledge-slices.md) (lane IX) slices it:

| Built (with the tests in `DbDataRunner.Tests` and the `DbDataKit.Runner*` test projects) | Not built |
| --- | --- |
| The kit a .NET job writes against (`DbDataKit.Runner.Kit`: typed progress, steps and units, connection slots, cancellation, logs with secrets redacted) and its test harness (`DbDataKit.Runner.Kit.Testing`, `RunSimulator`) | Clients as libraries: HTTP with sign-in and paging, SFTP and SCP, FTPS, SMB; readers and writers for CSV, JSON Lines, XML, fixed width, Excel, compressed and encrypted files |
| Checkpoints, a ledger of processed files, idempotency keys, a store for refused records, retries with back-off for temporary failures, concurrency limits per package and per connection | Connection kinds SFTP, SCP, FTPS, SMB, object storage, and OAuth 2 authorization code (the `http` kind with API key, basic, OAuth 2 client credentials and client certificates is built, IX01 and IX02, in `DbDataKit.Connections.Http`); a task's slots bind suite connections; the file trigger's seam (`IFileSource`) has a local folder as its only source |
| Triggers: schedule, after another run, webhook (signed, replays refused, into the inbox; the runner's end), file arrival (local folder), a DbDataStore change (the engine's half: a feed source, once per look), by hand | The serving layer's end of webhooks; the client of a Store's change feed; chained runs of Sync and Build (they move onto the runner later, plan 28 section 8) |
| Deployment lineage of packages, versions, channels, runners and runs | Lineage nodes for outside systems ("read from" and "wrote to" calls); landing helpers (staging tables through `DbDataKit.Transfer`, DbDataStore rows through its API, documents); change detection as a library; dry runs; project templates |
| Posture choices and per-channel connection bindings | The rule that an outbound step in a non-production channel uses that channel's connection or is refused |

**[`docs/integrations.md`](../docs/integrations.md) is the one page for integrations** (what one is, the package kinds, the manifest, authoring, build and test, publish, and what is built per part). A csx integration (C# files with `#r "nuget:"` lines) is built and tested with `dbdatarunner package build` and `package test`, which need only the .NET runtime, not the SDK; a compiled .NET project needs the SDK. Both are authored in a workspace as an `integration` project (IX20, IX24) or published by hand with `package publish`.

## What a task reports it read and wrote

A task says what it touched with the Kit's `run.Lineage(reads, writes)`: plain strings, kept per task in the runner's state (`task_lineage` of the `runner` module: one row per task, direction and name, with the run that said it last; a rerun adds no row). Two kinds of line have a meaning (plan 27a IX12, plan 24a UC13):

- `external://<kind>/<name>` (for example `external://http-api/crm`, `external://sftp/exports`): an outside system. `GET /api/v1/lineage` (and `dbdatarunner lineage graph`) lays a job node under the task's package and an outside-system node for each one, so `lineage provenance` of a table the task wrote shows the API upstream. A task whose systems would close a circle with another's is left out and named in `problems`.
- a dataset identity (`engine://server/database.schema.table`, what `LineageIdentity.Dataset` gives): when the run ends, the runner tells the serving groups it is paired with (`dbdatarunner gateway add`, the console's Link, or `POST /api/v1/gateways`) that the dataset changed, so a Gateway empties the cached answers that read it. The runner signs the message as itself with the role Publisher; a failed delivery never changes the run; with no pairing nothing is sent.

Any other text is the task's own name for a table: it is kept and is not a node.
