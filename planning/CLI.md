# CLI: the programs we ship and the commands they keep

## Goal

`dbdatabuild`, `dbdatasync` and `dbdatastore` are removed. The programs that run the four services remain: `dbdatakit` (the console),
`dbdataworkspace` (the Workspace), `dbdatarunner` (the Runner) and `dbdatagateway` (the Gateway); whether they become one program is the open design
"One program for every service, or one per service". Each keeps only the commands that run and configure its service. The Workspace and the Runner
call Build, Sync and the Store as libraries; what a person did with a command is an operation in the console or the Workspace (AGENTACCESS).

Build was first written as a command line in a user's own git workspace; that has not been a goal since it joined the suite (owner, 2026-11-19).

## What exists today

- The Workspace starts our own programs and parses their JSON output (`ChildProgram` in `DbDataKit.Authoring.Server`): `BuildTool` (`dbdatabuild`:
  create a project from a template, `project show metadata` to check it, `project publish --to runner|gateway --out` to package it), `SyncTool`
  (`dbdatasync`) and the integration kind's `dbdatarunner`. `docs/architecture.md` ("How our code calls our code") already says new code does not
  do this; these calls are older.
- A Runner runs Build, Sync and Store tasks from runtime packages (`dbdatabuild-runtime`, `dbdatastore-runtime`, ...) that contain the program and a
  manifest. Build's Runner jobs (`RefreshJob`, `OperationJobs`, `ProjectPublish`) live in the `DbDataBuild.Cli` project.
- The three programs also have commands a person runs on a project folder (Build's `project sample`, `project seed`, `connection seed`, its tests and
  checks; Sync's and the Store's own nouns).
- The four service programs carry nouns beyond running and configuring the service (the Gateway's `query`, `semantic`, `apps`; the Runner's `package`;
  the console's `knowledge`, `release`; and others); no list of them exists.
- INSTALLER-7 moves the admin commands of the four services onto each running service's local API; it stays as it is.

## Tasks

| Task | What | Depends on | Size | Done when |
| --- | --- | --- | --- | --- |
| CLI-1 | The list: every noun and verb of the seven programs, each marked keep (runs or configures a service), operation (what replaces it, an AGENTACCESS operation or a screen), or remove; who calls it today (a person, the Workspace, a Runner, CI, a test); written into this file as the tasks below, per product | none | S | the list is in this file and the owner has read it |
| CLI-2 | The Workspace calls Build in process: the engine of create, check and package moves to `Shared/src/DbDataKit.*` (adapter first in DbDataBuild); `BuildTool` is replaced | CLI-1 | M | the Workspace's build-kind tests pass unchanged with no `dbdatabuild` on the machine |
| CLI-3 | The same for Sync (`SyncTool`) | CLI-1 | M | the Workspace's sync-kind tests pass with no `dbdatasync` on the machine |
| CLI-8 | Building and testing an integration project: the Workspace compiles a csx or compiled integration in process with the shared package build library, which runs none of the author's code; Test starts the project's test task as a run on a Runner in a non-production channel and shows its result in the Workspace; `IntegrationProjectKind` starts no `dbdatarunner` and no `dotnet` that runs the author's code; the in-process NuGet restore takes the Workspace's outbound gate's handler (`OutboundGate.CreateHttpHandler("nuget.restore")`), so it follows the Workspace's outbound list | none | M | the Workspace's integration tests pass with no `dbdatarunner` on the machine; a test script that writes a file leaves no file on the Workspace and its run is in the Runner's record for the `dev` channel |
| CLI-4 | The runtime packages carry a task host the Runner loads by its task protocol, not a command-line program; Build's Runner jobs leave `DbDataBuild.Cli` | CLI-2 | L | a Runner runs a Build refresh, a Sync replication and a Store deploy from the new packages; a package with the old program is refused naming the new one |
| CLI-5 | Each verb the list marks operation becomes one (AGENTACCESS-1) or a screen, area by area, as the list orders them | CLI-1, AGENTACCESS-1 | L | per area: the operation's test; the verb answers with the sentence naming where it went |
| CLI-6 | Remove `dbdatabuild`, `dbdatasync` and `dbdatastore` as programs, and the service programs' verbs the list marks remove; their packaging in `dist/` (MSI, Scoop, install scripts), their docs and tests | CLI-2, CLI-3, CLI-4, CLI-5 | M | `dist/` builds no package for the three; the service programs' removed verbs answer with their sentence (`RemovedCommands`); CI green |
| CLI-7 | Docs: the "Planned architecture changes" section of `docs/architecture.md` thinned as each task lands and what is built described in its place; the products' READMEs | each task | S | `check_doc_inputs.py` passes; the section lists only what is still to come |

## Needs from other subjects

- AGENTACCESS-1: the operation, for CLI-5.
- AGENTACCESS-13 removes `ui mcp` from every program; CLI does not repeat it.
- INSTALLER-7: the admin commands on the local API; CLI keeps what it moves.
- SAMPLEDATA: Build's seeds and generator move to `Shared/` there; their commands (`project seed`, `project sample`, `connection seed`) are listed in
  CLI-1 like the others.

## Open designs

### Framework-dependent or self-contained programs

**What.** Whether the programs the suite still ships run on an installed .NET runtime (framework-dependent: smaller downloads, the runtime patched by
the operating system's updates) or carry their own (self-contained: nothing to install first). The 32-bit ODBC worker stays self-contained. Today
the packaging is `scripts/publish.sh`, `build/publish.ps1`, `package.ps1` and `requiresDotnet` in `dist.json`.

**Why here.** It is decided once the list says which programs remain: `dbdatabuild` is removed as a program, the Gateway's command line may be, and
PLUGINS may make one program of the four services.

**Open questions.** Which programs remain; whether an install checks for the runtime and says what to install; the download sizes either way.

### One program for every service, or one per service

**What.** `dbdatakit <role> run` starts one role: `console`, `workspace`, `runner` or `gateway`. The role is fixed in the install's settings and on the
service's registration (the Windows service name, the systemd unit), so a wrong flag cannot start a different role. One process runs one role; the
program refuses to start a second role in the same process, and refuses a role whose folder or account belongs to another role. Each role keeps its own
process, OS account, keys, state database and listeners, as `docs/architecture.md` describes for the four services today. A developer install runs the
four roles as four processes of the same build, which is what CLOUDDEV's development instance starts. The Runner's task host and the Gateway's 32-bit
ODBC worker stay separate programs: one isolates each task, the other loads 32-bit drivers.

**Why.** One version to install and upgrade, for a developer and for a service install; one startup path to harden; one program in place of seven.

**Open questions.**

- The command layout: which commands stay on the one program besides `<role> run` and the role's configuration (CLI-1's list decides what is
  kept at all).
- Install folders: one folder per role, so the console can still upgrade one role at a time, or one shared folder that upgrades every role on a
  machine together.
- Rules keyed on the program file: Windows Firewall, AppLocker and EDR rules by program path can no longer tell the roles apart; whether the install
  writes rules by service SID instead.
- Container images: one image with the role in an environment variable, or one image per role with only that role's plugins.


## Shared areas touched

`Shared/src/DbDataKit.Authoring.Server` (the project kinds), the runtime packages and their manifests, `dist/`, `DbDataKit.slnx`, the products'
READMEs, `docs/architecture.md`.
