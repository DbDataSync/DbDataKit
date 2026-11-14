# Architecture of the suite

The shape of the suite and the decisions behind it (they were made in plan 13, which this document replaces; its decision numbers D1 to D20 are kept
because source comments cite them). Where each part is described in detail: the product READMEs, the package READMEs under `Shared/src/`, and
`docs/suite-listeners-and-auth.md` (what listens where) and `docs/suite-data-flow.md` (data and control).

## Target shape

```
 people (browsers) ──► dbdatakit console  (its own host and command line: shell, sign-in screens, the servers it manages, their screens,
                       │                   cross-product views: connections, monitoring, notifications; docs of every module)
                       │  every call relayed by the console, as a signed token naming the person (D14, D16)
        ┌──────────────┼─────────────────┬───────────────────┬──────────────────┐
        ▼              ▼                 ▼                   ▼                  ▼
   Sync           Build            Store               Runner             serving layer (DbDataGateway)
   replication    many projects,   master data         scheduled and      the EDGE for end users: Flight SQL, semantic, store reads
   engine,        scheduler,       models in your      batch work, a      and writes, apps; hosts the IDENTITY HOME by default (D20)
   TaskRunner     monitoring,      own databases       process per task   │
   (child         authoring        │                   │                  │
   processes)     │                │                   │                  │
        └─────────┴────────────────┴───────────────────┴──────────────────┘
           each product: its own server with a management API on its own listener, its own state database, its own config directory;
           no web UI of its own. The console reaches a server only through that API, and a server never calls a console.
                                                   │
   Shared/ (shared libraries): Config · Secrets · Dialect · Connections (+ FlightSql) · Metadata · Transfer · Hosting · Scheduling ·
                                  Monitoring · Scripting (Roslyn + NuGet) · Cli (command surface, MCP) · Web (trust, sign-in) · State · Identity
```

- **The console and the products are separate** (D1). A product is a server (an operating-system service with its own config directory, state store and
  management API, and no UI of its own) plus a command line that works with or without the console. The console is the one UI, a host built from the
  shared web packages (`Shared/web`). It holds the list of registered servers and each product's screens. A product keeps running (schedules,
  replication, queries) while the console is down; only the screens are unavailable. One console manages several servers of each product, and one server
  can be paired with several consoles.
- **A standalone install** is one product plus the console with that product registered. Each product ships on its own (its image, MSI and zips); the
  console has its own MSI, and a product's MSI does not embed it. Pairing after installing both is a documented step (decided 2026-10-28; `dist/README.md`).
- **The serving layer is the edge** for end users (plan 20). Management traffic (the console) and data traffic (clients of the Gateway) use different
  listeners, and the management API is never on a hot path.
- **Identity home** (D20, plan 20): one identity component per install holds users, groups, role assignments and an OpenID Connect provider
  (`DbDataKit.Identity`). The serving layer hosts it by default, so end users sign in without reaching the console; it can run on its own
  (`dbdatakit identity run`). Consoles, apps and agents are OpenID Connect clients of it. Product servers have no user accounts: they accept a paired
  client's token, which names the person and their roles.
- **State.** Every server keeps its own state database through `DbDataKit.State` (SQLite by default; SQL Server and PostgreSQL for a group or an install).
- **Shared definitions.** One `connection/1` format, one secret reference syntax, one identity model and one git-tracked config layout, in the same code
  in every product, so a console can show and edit them side by side. Each server still owns its definitions: it must run without the console and without the
  other products.

## Screens follow the work, not the servers (owner, 2026-11-14)

A screen is organised around what a person is doing (author a model, deploy it, look at its runs, approve a change) and the product it belongs to, never around
which server does the work. **Where an action is started and where it runs are separate questions**, and the screen answers only the first:

- **Started where the person is working.** Creating or migrating a Store model's tables is a **Deploy** on the model's screen; applying a Build plan, provisioning a
  Sync target, testing a connection, a refresh or a run-now are buttons where the project or model is shown. The person does not go to another server's page to do it.
- **Run where it belongs.** The work itself runs as a task on the Runner (the login is bound there, the run is recorded there under the person's name), or is answered by
  the Gateway, the Workspace or the console. That is an implementation detail the screen may mention (a link to the run), not a place the person has to find.
- **Results come back to the same screen.** A run's status, log and outcome are shown where it was started, with a link to the full run on the Runner's screens and a link back.

So very few screens stay server- or service-oriented: the console's **Servers** pages are for administering the installation (pairing, versions, health, certificates,
state, upgrades), not for doing work. A screen that makes a person find "the server", then "the runner", to finish one task is a defect. New screens are planned this way
from the start, and the existing server-oriented screens move to it as their slices are touched (plan 33 SC20o, SC22l, `docs/authoring-parity.md`).

## Our code calls our code as a library (owner, 2026-11-14)

New code calls another part of the suite **in process, as a library**, never by starting one of our own command-line programs and parsing what it prints. A separate
process is used only for:

- an **external** program (git, a database's own tool, a browser);
- the **Runner's task isolation**, for resilience and security (a task runs in its own process; a runtime package is loaded as an assembly where it can be, so the contract is the Runner's task protocol, not a command line);
- **code a person wrote** (Sync's scripts, an integration's code);
- **32-bit drivers** (the Gateway's 32-bit ODBC worker).

**New code goes into a shared library first (owner, 2026-11-14).** Code that can reasonably be shared is written as a library under `Shared/src` (`DbDataKit.*`),
and the products' command-line and service projects **compose** those libraries: a host project wires settings, routes, commands and services together and does not hold the
logic itself. A product keeps code of its own only when nothing else could use it, and says so. Code that exists today moves when a slice touches it, not for its own sake.

In those cases the process is a host with a typed protocol. The calls that exist today (the Workspace's `BuildTool` and `SyncTool`, the Runner starting the Build and
Sync runtimes by their command lines) stay until a slice touches them anyway; nothing is converted for its own sake. Whether `dbdatabuild`, `dbdatasync` and
`dbdatastore` stay as command lines is then only a question of a tool for people.

## Clean break

The new releases start from nothing. No migration, import, upgrade path or compatibility reader is written for existing configs, scripts, libraries,
drivers, secrets, users, sessions or state databases, and no legacy names are kept (old CLI forms, old environment variable names such as
`DBDATAKIT_BUILD_*` and `DBDATAKIT_SECRET_*`). Instead, a server or CLI that finds a file in an old format (no `schemaVersion`, an older one,
`appsettings.json` where `server.yaml` belongs, a `.cs` script, a `library.json`) **refuses to start or to load it, with a message naming the file and
pointing to the documentation**. Release notes say plainly that an existing install is set up again. Users' own data (their databases and DuckDB files)
is not ours to break and is outside this rule. (CLAUDE.md states the working rule: never silent, and a test says so.)

## Decisions

| # | Decision | Why |
| --- | --- | --- |
| D1 | The console and the products are separate: servers with a management API and no UI; one shared console UI. | A product must keep working with the console down; one UI avoids three web apps. |
| D2 | One YAML format for every product (Gateway moved off JSON); no migration from `appsettings.json`. | One reader, one editor, one history; the clean break makes migration code unnecessary. |
| D3 | Each product keeps its own CLI; `dbdatakit` is the console's CLI only; lower-case executables; `<noun> <verb>` everywhere, old forms removed. | One grammar to learn; a console CLI that forwarded to products would hide which server a command touches. |
| D4 | Each product is a standalone install option; the suite release (console image and MSI included) comes from the same workflow and commit; separate MSIs, pairing at install is a documented step. | Nothing forces a user to install products they do not use; one workflow keeps the cost low (`docs/releases.md`). |
| D5 | The updater is deleted (plan 08, the six `DbDataKit.Update*` packages and Sync's updater). Images, MSI and the install scripts are the distribution. | The updater was shelved and never settled; the other routes cover installing and upgrading. |
| D6 | DuckDB 2.0.0 at once with the released engine pinned (`duckdb.pin`) and DuckDB.NET packages that carry no engine. Done. | Waiting for DuckDB.NET 2.0 would hold every product back. |
| D7 | No DuckDB 1.x after 2.0; code that existed only for 1.x is deleted. Done. | One engine to test. |
| D8 | Anything Build cannot reproduce exactly on DuckDB 2.0 is refused with a named diagnostic; `%` and `//` by zero follow 2.0 and the targets (they raise). Done. | A refusal is honest; a silent difference is a data bug. |
| D9 | Our own Roslyn scripting with NuGet libraries, embedded, no SDK at run time (`DbDataKit.Scripting`). | `dotnet script` and its embedded form both need `dotnet restore`, hence the SDK. |
| D10 | Secret store: encrypted values in a YAML file, key protected by DPAPI on Windows and a permission-restricted file elsewhere; no ClrKernel, nothing imported. | Works for services, CLIs and containers alike; an OS keychain is per user. |
| D11 | **Decided (owner, 2026-11-14): framework-dependent everywhere.** Every program of the suite needs the .NET 10 runtime (ASP.NET Core for the servers) installed on the machine, as the console, Runner, Sync, Store and Workspace already do; Build's single self-contained file and the Gateway's self-contained Windows build become framework-dependent like the rest (plan 33 SC25). **One exception:** the Gateway's 32-bit ODBC worker (`DbDataGateway.Worker`, win-x86) stays self-contained, so a machine with 32-bit drivers needs no second, 32-bit .NET runtime. | One prerequisite for every program, one publish mode in the scripts, smaller zips and runtime packages, and .NET's security fixes arrive through one runtime the machine updates (Microsoft Update on Windows, the package manager on Linux) instead of a rebuild of the suite. |
| D12 | **Revised (owner, 2026-11-14):** isolating Build's native SQL library (polyglot) in a child process is not required: no crash has been reported and the library is pinned and hash-checked. Existing child-process calls stay until a slice touches them; see "Our code calls our code as a library". | A defensive cost with no reported failure behind it. |
| D13 | A Build schedule lives in the project (committed with the refresh plan); an operator may pause or run now, not add. | Fits "everything is a file" and review of plans. |
| D14 | Trust: a client calls a server, never the reverse; several clients per server, each with its own key and ceiling; short-lived signed tokens naming a person; ownership-scoped ceilings (Publisher) for server clients; the local CLI over a named pipe or Unix socket. | A server needs no credentials for any console and stays the security boundary (`DbDataKit.Web`). |
| D15 | Authoring merges by pushing to the project's git remote and opening a pull request when one is configured, else inside the Build server with the author's identity. | The usual review path where a remote exists. |
| D16 | The console's back end relays every browser call to the servers; browsers never reach them. | Servers stay off the browser's network, pinned self-signed certificates work, no CORS, and live updates (SignalR) are relayed. |
| D17 | A console and a server can differ in version: the management API carries an integer version, a client supports its own and the one before, and says which side to upgrade (since extended to the suite's compatibility generation, N and N-1). | Products release on their own. |
| D18 | The Gateway has a state store (SQLite through `DbDataKit.State`) for its audit, notifications and per-console read state. | It had none. |
| D19 | A Build server's projects come from a local path or a git remote and branch it clones and pulls before each scheduled run. | Both ways of working exist. |
| D20 | One identity home per install, amended by plan 20: hosted by the serving layer by default. | End users must sign in without reaching the console; one list of people, roles assigned once. |

### Decisions recorded after plan 13

Decided by the owner after the table above was closed. The numbers are the ones the old follow-up list gave them (the plan is finished and deleted) and that source comments cite as `13c D<n>`; the details are in the README named in the last column.

| # | Decision | Why | Details |
| --- | --- | --- | --- |
| 13c D12 | Relationships between built models are written as `foreign_keys:` on the model; semantic relationships are read from them. | One spelling for a foreign key in both formats. Whether models and semantic models become one format is a proposal (plan 19 J30). | `docs/semantic/models-and-semantic-models.md` |
| 13c D20 | Sync has no sign-in of its own: it accepts a console's token and its task runner's token and answers `/api/health`. A `server.yaml` that sets `Auth:Windows` or `Auth:Network` is refused with a message naming the file. | One place to sign in (the console, or the identity home behind it); nothing to secure twice. | `DbDataSync/docs` |
| 13c D22 | Installing a library adds `libraries/*/lib/` to the config repository's `.gitignore` (`LibraryPaths.EnsureStoreGitignored`). | The extracted packages are a local store; their DLLs must not be committed beside `library.lock`. | `DbDataSync/docs/drivers-and-libraries.md` |
| 13c D24 | A person's external (Entra, Windows) groups count until their next sign-in through that source: no expiry, and signing out does not clear them. | The home records what the last sign-in carried; a person removed from a group at the provider loses the role at the next sign-in. A lifetime can be added if a tenant needs one. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D25 | Entra's group overage (more than 200 groups, no `groups` claim) is left to the tenant: its administrator limits the groups with `groupMembershipClaims`; the home calls no Microsoft Graph. | Graph needs a credential and a permission of its own. Revisit if a tenant cannot limit its groups. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D26 | An Entra sign-in links to an existing account on the token's UPN (preferred) or email, trusted (`trustEmail`); other OpenID sources link only on a verified address. | An Entra id token has no `email_verified`; a tenant that lets people set their own mail attribute must not be trusted, so it is a setting. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D27 | Copying a connection to another server never copies a secret value: the copy names the reference the target must hold (for a Sync target the person types the password, sent to that server only). | The console never reads a secret. | `DbDataKit/README.md` |
| 13c D28 | Sync's `extensions.sync` keys use the shared snake_case names (`segment_order`, `test_query`, `connect_timeout_seconds`, `command_timeout_seconds`), and its connection file API takes `{text, expected_version, password}` and answers a conflict with `current_version`, as the Gateway's data-source API does. | One spelling and one shape for the pair of file APIs the console copies between. | `DbDataSync/docs/configuration.md` |
| 13c D29 | The slow test engines' images (BigQuery emulator, sqlflite, SQL Server 2025, Spark) are pinned by digest in `testenv/docker-compose.yml`; the file says how to move one. | A moving tag can turn the nightly run red without a change of ours. | `testenv/docker-compose.yml` |
| 13c D30 | The identity home's admin API is the management API of whatever hosts the home; the console's screens use it through its relay and the commands stay. | One admin surface per host, no screens of the home's own. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D31 | A home that runs on its own keeps the framework key ring in its `dataprotection/` directory (a backup holds the state and that directory); the serving layer's home protects its signing keys with `KeySigningKeyProtector` and a key in its secret store. | Simple for one home; a group of homes on one shared state needs the same key everywhere. | `DbDataKit/README.md`, `Shared/src/DbDataKit.Identity/README.md` |

## Why the design is safe

| Risk | Answer |
| --- | --- |
| The secrets key is lost (disk, rebuild) | Documentation and `server setup` say to back it up separately from `secrets.yaml`; `secret export` moves values between machines; the store is useless without the key, by design. |
| The secrets key is readable by another local user | The server refuses to start on loose permissions; DPAPI plus an access list on Windows; keys from the platform's secret mechanism in containers. |
| A stolen console signing key acts on every server paired with that console | The key never leaves the console's protected store; role ceilings limit the damage; `server unpair` on each server, or a signed key rotation, cuts it off. |
| Authoring lets a web user write files that a scheduled run then executes | Drafts live in per-user worktrees; schedules run from a pinned ref; merging needs a role; every write goes through the confined `project file` commands. |
| The console holds every product's credentials to manage it | Per-server pairing, short-lived tokens scoped to one server, ceilings per console, no database secrets in any console (each server resolves its own references). |
| A management API weakens the Gateway's security | A separate listener, HTTPS with a pinned certificate, signed tokens checked on every call, roles enforced on the server, no shared code on the query path, the same failure classification and redaction. |
| Two consoles change the same thing | Every write is checked against the version it was based on and refused when stale (`WriteGuard`). |
| Monitoring depends on the console being up | Each server evaluates its own health rules and keeps its own notifications; the console only shows them. |
| A schedule runs twice | The per-load lock and `on_busy` cover a manual run; the work queue's lease and run lock cover two servers; a project is hosted by one server at a time. |

## Where the conventions live

| Convention | Documented in |
| --- | --- |
| Command grammar, effect classes, JSON output, exit codes, MCP | `Shared/src/DbDataKit.Cli/README.md` |
| Pairing, tokens, ceilings, roles, audit, version rules | `Shared/src/DbDataKit.Web/README.md`, `Shared/src/DbDataKit.Hosting.Local/README.md` |
| Browser relay, instances, connections across servers | `DbDataKit/README.md` |
| Identity home, OpenID Connect, tokens | `Shared/src/DbDataKit.Identity/README.md` |
| Schedules and the work queue; run model and health rules | `Shared/src/DbDataKit.Scheduling/README.md`, `Shared/src/DbDataKit.Monitoring/README.md` |
| Secret store | `Shared/src/DbDataKit.Secrets.Store/README.md`, `Shared/src/DbDataKit.Secrets.Cli/README.md` |
| Config layout, connection format | `Shared/src/DbDataKit.Config.Git/README.md`, `Shared/src/DbDataKit.Connections.Abstractions/README.md` |
| Scripting, libraries, drivers | `Shared/src/DbDataKit.Scripting/README.md` |
| Integrations: what they are, package kinds, authoring, build (csx with no SDK), test, publish; what is built and what is not | `docs/integrations.md` |
| Releases and distribution | `docs/releases.md`, `dist/README.md` |
