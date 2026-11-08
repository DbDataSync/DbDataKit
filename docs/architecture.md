# Architecture of the suite

The shape of the suite and the decisions behind it (they were made in plan 13, which this document replaces; its decision numbers D1 to D20 are kept
because source comments cite them). Where each part is described in detail: the product READMEs, the package READMEs under `DbDataKit/src/`, and
`docs/suite-listeners-and-auth.md` (what listens where) and `docs/suite-data-flow.md` (data and control).

## Target shape

```
 people (browsers) ──► dbdatakit console  (its own host and command line: shell, sign-in screens, the servers it manages, their screens,
                       │                   cross-product views: connections, monitoring, notifications; docs of every module)
                       │  every call relayed by the console, as a signed token naming the person (D14, D16)
        ┌──────────────┼─────────────────┬───────────────────┬──────────────────┐
        ▼              ▼                 ▼                   ▼                  ▼
   Sync           Build            Store               Runner             serving layer (DbDataProxy, to be renamed DbDataGateway)
   replication    many projects,   master data         scheduled and      the EDGE for end users: Flight SQL, semantic, store reads
   engine,        scheduler,       models in your      batch work, a      and writes, apps; hosts the IDENTITY HOME by default (D20)
   TaskRunner     monitoring,      own databases       process per task   │
   (child         authoring        │                   │                  │
   processes)     │                │                   │                  │
        └─────────┴────────────────┴───────────────────┴──────────────────┘
           each product: its own server with a management API on its own listener, its own state database, its own config directory;
           no web UI of its own. The console reaches a server only through that API, and a server never calls a console.
                                                   │
   DbDataKit/ (shared libraries): Config · Secrets · Dialect · Connections (+ FlightSql) · Metadata · Transfer · Hosting · Scheduling ·
                                  Monitoring · Scripting (Roslyn + NuGet) · Cli (command surface, MCP) · Web (trust, sign-in) · State · Identity
```

- **The console and the products are separate** (D1). A product is a server (an operating-system service with its own config directory, state store and
  management API, and no UI of its own) plus a command line that works with or without the console. The console is the one UI, a host built from the
  shared web packages (`DbDataKit/web`). It holds the list of registered servers and each product's screens. A product keeps running (schedules,
  replication, queries) while the console is down; only the screens are unavailable. One console manages several servers of each product, and one server
  can be paired with several consoles.
- **A standalone install** is one product plus the console with that product registered. Each product ships on its own (its image, MSI and zips); the
  console has its own MSI, and a product's MSI does not embed it. Pairing after installing both is a documented step (decided 2026-10-28; `dist/README.md`).
- **The serving layer is the edge** for end users (plan 20). Management traffic (the console) and data traffic (clients of the Proxy) use different
  listeners, and the management API is never on a hot path.
- **Identity home** (D20, plan 20): one identity component per install holds users, groups, role assignments and an OpenID Connect provider
  (`DbDataKit.Identity`). The serving layer hosts it by default, so end users sign in without reaching the console; it can run on its own
  (`dbdatakit identity run`). Consoles, apps and agents are OpenID Connect clients of it. Product servers have no user accounts: they accept a paired
  client's token, which names the person and their roles.
- **State.** Every server keeps its own state database through `DbDataKit.State` (SQLite by default; SQL Server and PostgreSQL for a group or an install).
- **Shared definitions.** One `connection/1` format, one secret reference syntax, one identity model and one git-tracked config layout, in the same code
  in every product, so a console can show and edit them side by side. Each server still owns its definitions: it must run without the console and without the
  other products.

## Clean break

The new releases start from nothing. No migration, import, upgrade path or compatibility reader is written for existing configs, scripts, libraries,
drivers, secrets, users, sessions or state databases, and no legacy names are kept (old CLI forms, old environment variable names such as
`DBDATA_BUILD_*` and `DBDATASYNC_SECRET_*`). Instead, a server or CLI that finds a file in an old format (no `schemaVersion`, an older one,
`appsettings.json` where `server.yaml` belongs, a `.cs` script, a `library.json`) **refuses to start or to load it, with a message naming the file and
pointing to the documentation**. Release notes say plainly that an existing install is set up again. Users' own data (their databases and DuckDB files)
is not ours to break and is outside this rule. (CLAUDE.md states the working rule: never silent, and a test says so.)

## Decisions

| # | Decision | Why |
| --- | --- | --- |
| D1 | The console and the products are separate: servers with a management API and no UI; one shared console UI. | A product must keep working with the console down; one UI avoids three web apps. |
| D2 | One YAML format for every product (Proxy moved off JSON); no migration from `appsettings.json`. | One reader, one editor, one history; the clean break makes migration code unnecessary. |
| D3 | Each product keeps its own CLI; `dbdatakit` is the console's CLI only; lower-case executables; `<noun> <verb>` everywhere, old forms removed. | One grammar to learn; a console CLI that forwarded to products would hide which server a command touches. |
| D4 | Each product is a standalone install option; the suite release (console image and MSI included) comes from the same workflow and commit; separate MSIs, pairing at install is a documented step. | Nothing forces a user to install products they do not use; one workflow keeps the cost low (`docs/releases.md`). |
| D5 | The updater is deleted (plan 08, the six `DbDataKit.Update*` packages and Sync's updater). Images, MSI and the install scripts are the distribution. | The updater was shelved and never settled; the other routes cover installing and upgrading. |
| D6 | DuckDB 2.0.0 at once with the released engine pinned (`duckdb.pin`) and DuckDB.NET packages that carry no engine. Done. | Waiting for DuckDB.NET 2.0 would hold every product back. |
| D7 | No DuckDB 1.x after 2.0; code that existed only for 1.x is deleted. Done. | One engine to test. |
| D8 | Anything Build cannot reproduce exactly on DuckDB 2.0 is refused with a named diagnostic; `%` and `//` by zero follow 2.0 and the targets (they raise). Done. | A refusal is honest; a silent difference is a data bug. |
| D9 | Our own Roslyn scripting with NuGet libraries, embedded, no SDK at run time (`DbDataKit.Scripting`). | `dotnet script` and its embedded form both need `dotnet restore`, hence the SDK. |
| D10 | Secret store: encrypted values in a YAML file, key protected by DPAPI on Windows and a permission-restricted file elsewhere; no ClrKernel, nothing imported. | Works for services, CLIs and containers alike; an OS keychain is per user. |
| D11 | **Open.** Whether suite binaries are self-contained or framework-dependent is not decided. Owner (2026-11-08): "we need to re-evaluate this plan and come up with a decision, particularly after the sync and build tools use the runner". What ships today: the console, Sync and Runner zips are framework-dependent (they need the .NET 10 runtime: `requiresDotnet` in `dist.json`); Build ships self-contained single files. To be decided after Sync and Build run on the runner (plan 28c E06 to E13). | Fewer prerequisites on a server against smaller zips; the answer depends on how the runner hosts the tools. |
| D12 | The Build server runs commands in child processes (one per command, one long-lived per authoring workspace). | A crash in the native polyglot library must not take the server down. |
| D13 | A Build schedule lives in the project (committed with the refresh plan); an operator may pause or run now, not add. | Fits "everything is a file" and review of plans. |
| D14 | Trust: a client calls a server, never the reverse; several clients per server, each with its own key and ceiling; short-lived signed tokens naming a person; ownership-scoped ceilings (Publisher) for server clients; the local CLI over a named pipe or Unix socket. | A server needs no credentials for any console and stays the security boundary (`DbDataKit.Web`). |
| D15 | Authoring merges by pushing to the project's git remote and opening a pull request when one is configured, else inside the Build server with the author's identity. | The usual review path where a remote exists. |
| D16 | The console's back end relays every browser call to the servers; browsers never reach them. | Servers stay off the browser's network, pinned self-signed certificates work, no CORS, and live updates (SignalR) are relayed. |
| D17 | A console and a server can differ in version: the management API carries an integer version, a client supports its own and the one before, and says which side to upgrade (since extended to the suite's compatibility generation, N and N-1). | Products release on their own. |
| D18 | The Proxy has a state store (SQLite through `DbDataKit.State`) for its audit, notifications and per-console read state. | It had none. |
| D19 | A Build server's projects come from a local path or a git remote and branch it clones and pulls before each scheduled run. | Both ways of working exist. |
| D20 | One identity home per install, amended by plan 20: hosted by the serving layer by default. | End users must sign in without reaching the console; one list of people, roles assigned once. |

## Why the design is safe

| Risk | Answer |
| --- | --- |
| The secrets key is lost (disk, rebuild) | Documentation and `server setup` say to back it up separately from `secrets.yaml`; `secret export` moves values between machines; the store is useless without the key, by design. |
| The secrets key is readable by another local user | The server refuses to start on loose permissions; DPAPI plus an access list on Windows; keys from the platform's secret mechanism in containers. |
| A stolen console signing key acts on every server paired with that console | The key never leaves the console's protected store; role ceilings limit the damage; `server unpair` on each server, or a signed key rotation, cuts it off. |
| Authoring lets a web user write files that a scheduled run then executes | Drafts live in per-user worktrees; schedules run from a pinned ref; merging needs a role; every write goes through the confined `project file` commands. |
| The console holds every product's credentials to manage it | Per-server pairing, short-lived tokens scoped to one server, ceilings per console, no database secrets in any console (each server resolves its own references). |
| A management API weakens the Proxy's security | A separate listener, HTTPS with a pinned certificate, signed tokens checked on every call, roles enforced on the server, no shared code on the query path, the same failure classification and redaction. |
| Two consoles change the same thing | Every write is checked against the version it was based on and refused when stale (`WriteGuard`). |
| Monitoring depends on the console being up | Each server evaluates its own health rules and keeps its own notifications; the console only shows them. |
| A schedule runs twice | The per-load lock and `on_busy` cover a manual run; the work queue's lease and run lock cover two servers; a project is hosted by one server at a time. |

## Where the conventions live

| Convention | Documented in |
| --- | --- |
| Command grammar, effect classes, JSON output, exit codes, MCP | `DbDataKit/src/DbDataKit.Cli/README.md` |
| Pairing, tokens, ceilings, roles, audit, version rules | `DbDataKit/src/DbDataKit.Web/README.md`, `DbDataKit/src/DbDataKit.Hosting.Local/README.md` |
| Browser relay, instances, connections across servers | `DbDataConsole/README.md` |
| Identity home, OpenID Connect, tokens | `DbDataKit/src/DbDataKit.Identity/README.md` |
| Schedules and the work queue; run model and health rules | `DbDataKit/src/DbDataKit.Scheduling/README.md`, `DbDataKit/src/DbDataKit.Monitoring/README.md` |
| Secret store | `DbDataKit/src/DbDataKit.Secrets.Store/README.md`, `DbDataKit/src/DbDataKit.Secrets.Cli/README.md` |
| Config layout, connection format | `DbDataKit/src/DbDataKit.Config.Git/README.md`, `DbDataKit/src/DbDataKit.Connections.Abstractions/README.md` |
| Scripting, libraries, drivers | `DbDataKit/src/DbDataKit.Scripting/README.md` |
| Releases and distribution | `docs/releases.md`, `dist/README.md` |
