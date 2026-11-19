# COMMANDLINES: the suite's programs only run and configure the four services

## Goal

`dbdatabuild`, `dbdatasync` and `dbdatastore` are removed. Four programs remain, one per service: `dbdatakit` (the console), `dbdataworkspace` (the
Workspace), `dbdatarunner` (the Runner) and `dbdatagateway` (the Gateway). Each keeps only the commands that run and configure its service. The Workspace and the Runner
call Build, Sync and the Store as libraries; what a person did with a command is an operation in the console or the Workspace (AGENTACCESS).

Build was first written as a command line in a user's own git workspace; that has not been a goal since it joined the suite (owner, 2026-11-19).

## What exists today

- The Workspace starts our own programs and parses their JSON output (`ChildProgram` in `DbDataKit.Authoring.Server`): `BuildTool` (`dbdatabuild`:
  create a project from a template, `project show metadata` to check it, `project publish --to runner|gateway --out` to package it), `SyncTool`
  (`dbdatasync`) and the integration kind's `dbdatarunner`. `docs/architecture.md` ("Our code calls our code as a library") already says new code does not
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
| COMMANDLINES-1 | The list: every noun and verb of the seven programs, each marked keep (runs or configures a service), operation (what replaces it, an AGENTACCESS operation or a screen), or remove; who calls it today (a person, the Workspace, a Runner, CI, a test); written into this file as the tasks below, per product | none | S | the list is in this file and the owner has read it |
| COMMANDLINES-2 | The Workspace calls Build in process: the engine of create, check and package moves to `Shared/src/DbDataKit.*` (adapter first in DbDataBuild); `BuildTool` is replaced | COMMANDLINES-1 | M | the Workspace's build-kind tests pass unchanged with no `dbdatabuild` on the machine |
| COMMANDLINES-3 | The same for Sync (`SyncTool`) | COMMANDLINES-1 | M | the Workspace's sync-kind tests pass with no `dbdatasync` on the machine |
| COMMANDLINES-8 | Building and testing an integration project (COMMANDLINES-P1, option A): the Workspace compiles a csx or compiled integration in process with the shared package build library, which runs none of the author's code; Test starts the project's test task as a run on a Runner in a non-production channel and shows its result in the Workspace; `IntegrationProjectKind` starts no `dbdatarunner` and no `dotnet` that runs the author's code | none | M | the Workspace's integration tests pass with no `dbdatarunner` on the machine; a test script that writes a file leaves no file on the Workspace and its run is in the Runner's record for the `dev` channel |
| COMMANDLINES-4 | The runtime packages carry a task host the Runner loads by its task protocol, not a command-line program; Build's Runner jobs leave `DbDataBuild.Cli` | COMMANDLINES-2 | L | a Runner runs a Build refresh, a Sync replication and a Store deploy from the new packages; a package with the old program is refused naming the new one |
| COMMANDLINES-5 | Each verb the list marks operation becomes one (AGENTACCESS-1) or a screen, area by area, as the list orders them | COMMANDLINES-1, AGENTACCESS-1 | L | per area: the operation's test; the verb answers with the sentence naming where it went |
| COMMANDLINES-6 | Remove `dbdatabuild`, `dbdatasync` and `dbdatastore` as programs, and the service programs' verbs the list marks remove; their packaging in `dist/` (MSI, Scoop, install scripts), their docs and tests | COMMANDLINES-2, COMMANDLINES-3, COMMANDLINES-4, COMMANDLINES-5 | M | `dist/` builds no package for the three; the service programs' removed verbs answer with their sentence (`RemovedCommands`); CI green |
| COMMANDLINES-7 | Docs: the "Planned architecture changes" section of `docs/architecture.md` thinned as each task lands and what is built described in its place; the products' READMEs | each task | S | `check_doc_inputs.py` passes; the section lists only what is still to come |

## Needs from other subjects

- AGENTACCESS-1: the operation, for COMMANDLINES-5.
- AGENTACCESS-13 removes `ui mcp` from every program; COMMANDLINES does not repeat it.
- INSTALLER-7: the admin commands on the local API; COMMANDLINES keeps what it moves.
- SAMPLEDATA: Build's seeds and generator move to `Shared/` there; their commands (`project seed`, `project sample`, `connection seed`) are listed in
  COMMANDLINES-1 like the others.

## Proposed decisions

### COMMANDLINES-P1: where an integration project is built and tested (awaiting the owner)

Today the Workspace runs `dbdatarunner package build` and `package test` as a child process to compile a csx integration and run its test scripts
(`IntegrationProjectKind`). Two things are wrong with it: the Workspace starts another service's program and parses its output, and `package test`
runs a person's code on the Workspace, while `docs/architecture.md` ("Isolation by trust") puts all trusted custom code on the Runner. Scenario: an
author edits `src/sync_tickets.csx` and presses Test in the Workspace.

- Option A: the Workspace compiles in process with the shared package build library (compiling runs none of the author's code); Test starts a run of
  the project's test task on a Runner in a non-production channel, and the Workspace shows its result.
- Option B: the Workspace compiles and tests in process. Simple, but the author's code runs in the Control place.
- Option C: keep the child process, started from a shared host program instead of `dbdatarunner`. Removes the cross-service call; the author's code
  still runs on the Workspace.

Recommendation: A. It matches where custom code runs everywhere else, and it is how CHANNELS already plans Build test targets and Sync trial runs.

Answer from the owner, 2026-11-19: "Option A is the only sensible option. A workspace service is intended to be highly sandboxed and should have
extremely reduced network access in a hardened environment, and should never have direct access to a secret." Acted on in COMMANDLINES-8.

## Shared areas touched

`Shared/src/DbDataKit.Authoring.Server` (the project kinds), the runtime packages and their manifests, `dist/`, `DbDataKit.slnx`, the products'
READMEs, `docs/architecture.md`.
