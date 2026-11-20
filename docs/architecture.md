# Architecture of the suite

How the four services fit together, what runs where, and the changes planned. Details are in each product's README, the READMEs under
`Shared/src/`, `docs/suite-listeners-and-auth.md` (what listens where) and `docs/install.md`.

## Terms

- **Service**: one of the four running server programs: the control service, the Workspace service, the Runner service and the Gateway service. Each
  has its own process, state database, keys and network rules.
- **Console**: the browser UI people work in. The control service serves it.
- **Control service**: the `dbdatakit` server program. It serves the console, relays its calls and deploys packages.
- **Screens**: parts of the console, grouped by the work they are for: the Workspace screens, the Runner screens, the Gateway screens and the
  administration screens. A screen is not a service: a Workspace screen is served by the control service and reaches any service through the relay.
- **Relay**: the control service passing a call from the console to one named service, as the signed-in person. The browser never calls a service
  directly.
- **External system**: a database, file store or application that DbDataKit connects to but does not run: a source or target database (SQL Server,
  PostgreSQL, Oracle and others), a file store (SFTP, SMB, object storage), or an application reached by its API (a CRM, a ticketing system). Each one
  is reached through a connection, with a login that a Runner or Gateway holds.
- **Holds a secret**: a service stores the plain value to use it. **Handles a secret**: the plain value is in a service's memory, state, log or audit,
  even briefly.

## The four services

| Service | Program | What it does | Secrets it holds |
| --- | --- | --- | --- |
| Control | `dbdatakit` | Serves the console, signs people in, manages the other services, relays the console's calls, pulls built packages from the Workspace service and deploys them to Runners and Gateways. The root of trust: it pairs every service and makes every group key. | its own keys and the group keys; no login to an external system |
| Workspace | `dbdataworkspace` | Keeps the projects people and agents author, in git workspaces. Pulls dependencies, builds and checks projects. Never executes code from a project: a project's scripts and tests run on a Runner. Never publishes, binds, approves or starts a run: the control service does those. | none, ever |
| Runner | `dbdatarunner` | Runs the tasks of deployed packages, each in its own process: Build refreshes, Sync replications, Store deploys, integrations, tests, DDL. Runs in groups. | the bulk-write, DDL and integration logins its tasks use |
| Gateway | `dbdatagateway` | Serves end users: Flight SQL, semantic models, apps, store reads and writes, MCP for end users. Hosts the identity home by default. Runs in groups. | read logins and the Store's row-write login |

- **Each service keeps running when the control service is down**: schedules, replications and queries go on. Only the console is unavailable.
- **One control service manages several services of each kind**, and one service can be paired with several control services.
- **Management and data use separate listeners.** The control service calls a service's management listener; end users and external tools call the
  Gateway's data listeners.
- **Installing**: each service installs on its own, or all four on one machine with `dbdatakit-full`. The control service makes a single-use command
  that joins a service to it (`docs/install.md`).

## Who calls whom

- **Browser**: calls the control service only.
- **Control service**: calls the Workspace, Runner and Gateway services (relay, deploy, bind, start runs, approve).
- **Workspace service**: called only by the control service. Calls only package sources (NuGet, npm) and git remotes. It never calls another of our
  services and never handles a secret.
- **Gateway group and Runner group**: a Gateway group asks its linked Runner group to run a task, through an outbox. A Runner group tells its Gateway
  group that data changed and publishes definitions to it. The control service makes that link.
- **Runner to external systems**: a Runner connects to source and target databases and other external systems directly, with the logins it holds. A
  replication or a build reads and writes this way. It goes through a Gateway only when the Gateway has something the Runner lacks:
  - a driver only the Gateway has, such as a 32-bit ODBC driver;
  - a database the Runner cannot reach on the network;
  - a slow link, where the Gateway's compression helps.
- **Gateway to external systems**: with the read logins and the Store's row-write login it holds.
- **Any service to the control service**: once, to join it.

Example: an author edits a build project on a Workspace screen and presses Publish. The control service asks the Workspace service for the built
package, then deploys it to the Runner group as that person. The Workspace service never talks to the Runner.

Example: an author sets up a development login for a connection. The screen sends the login through the relay to the Runner service, which holds it.
The Workspace service is only told which binding now exists.

## What runs where

- **The Gateway answers requests**: queries, semantic models, apps, views, end users' agents, and the Store's row writes, which are small and recorded
  under the person who made them. Scheduled work never runs on the Gateway.
- **The Runner runs everything scheduled or queued, and all code a person wrote** (integrations, actions, a project's scripts and tests), each task in
  its own process.
- **The control service and the Workspace service do not hold a login to an external system.**
- **Each service holds a login to its own state database only.**
- **A request to the Gateway that needs a Runner task waits up to 10 seconds** for the task to finish; after that it answers with a link to the run.
  Example: a "Re-send invoice" button on an app screen shows the result if the task ends within 10 seconds, otherwise a link to the run.
- **Reads made for a person run on a Runner and go through the Gateway as that person**: subscriptions, report catalogs, evaluations. The Gateway
  applies that person's access rules to what they read.
- **Webhooks arrive at the Gateway.** It checks the signature, keeps the body and passes it to a Runner, so no Runner needs an address reachable from
  outside.
- **A missing partner only warns.** When a Gateway group has no linked Runner group, or the reverse, both still start, Monitoring shows a warning, and
  requests wait in the outbox until the partner is there.

## Sign-in

- **The identity home** keeps people, groups and role assignments, and signs people in (OpenID Connect). The Gateway hosts it by default, so end users
  sign in without reaching the control service. It can also run on its own (`dbdatakit identity run`).
- **The console, apps and agents sign people in through the identity home.**
- **Services do not keep user accounts.** A service accepts a token from a paired client that names the person and their roles.

## State, backups and shared formats

- **Each service keeps its own state database**: SQLite by default, SQL Server or PostgreSQL for a group of services or a whole install. One database
  server can hold a database or schema, and a login, for each service and group.
- **One backup set holds every service's snapshot**, and the control service backs up the keys once.
- **Every service reads the same formats** for connections (`connection/1`), secret references, identity and its git-tracked configuration, with the
  same code, so the console shows and edits them the same way everywhere. Each service owns its own definitions and runs without the control service and
  without the other services.

## Screens

- **A screen is organised around what the person is doing**: authoring a model, deploying it, looking at its runs, approving a change.
- **An action starts where the person is working.** Deploying a Store model, applying a Build plan's DDL, testing a connection, a refresh and "run now"
  are buttons on the screen that shows the project or model.
- **The work runs on the service it belongs to**, usually as a Runner task under the person's name. The person does not go to another service's
  screens to start it.
- **The result comes back to the same screen**: status, log and outcome, with a link to the full run on the Runner screens.
- **The administration screens are the exception**: pairing, versions, health, certificates, state and upgrades of each service.

Example: an author opens a Store model and presses Deploy. The control service starts the deploy task on the Runner. The model's screen shows its
progress and result, with a link to the run.

## Risky actions

- **A person can do anything their access allows.** When an action carries a risk (reading production, running against production, overwriting
  someone else's work), the screen says so at the moment of the action, and the person can go on.
- **The warning names the risk and what is at risk**: the channel, the database, the object.
- **Every action is recorded under the person's name.**
- **An action is refused only when the person's access does not allow it, or when it cannot work** (a file format the service does not read, a binding
  without a login). The refusal names the rule.

Example: Ana authors a sync project whose source has no development copy. On the project's screen she picks the channel `prod` and presses Refresh.
The screen shows "This reads the production database `erp` with the login bound for `prod`; only its structure is read, no rows", and she goes on.
The Runner reads the structure as Ana, with production's read login, and the catalog records that it was read from `prod`.

## Agents

- **Where**: in the console (administrators and authors) and in the Gateway (end users).
- **How they connect**: an outside agent over MCP, or the agent built into the product, which uses a model provider's API.
- **As whom**: always the person who connected it, with that person's roles.

## Command lines

Each service's program only runs and configures its service. Everything else is done in the console or by an agent.

## How our code calls our code

- **One part of the suite calls another in the same process, as a library.** Our code never starts one of our own programs and parses what it prints.
- **A separate process is used only for**:
  - an external program: git, a database's own tool, a browser;
  - isolation, so a crash or a hang does not take the service down (each Runner task runs in its own process for this);
  - a security sandbox, so code cannot reach the service's memory, files or network;
  - a 32-bit ODBC driver on the Gateway.
- **Code that more than one part of the suite can use is a library under `Shared/src`.** A service's own project wires settings, routes and services
  together.

## Planned architecture changes

- **Planned.** The Workspace service stops calling Runners and Gateways. Today it publishes packages, starts runs, reads catalogs and holds publisher
  keys for them. The control service will do all of that, pulling the built package from the Workspace service.
- **Planned.** The Workspace service runs with an outbound allow-list of package sources and git remotes only.
- **Planned.** Development logins go from the screen through the relay to the Runner. Today the plan has the Workspace service send them.
- **Planned.** DDL (creating, altering or dropping tables) runs as a Runner task that a person starts from the console. The control service starts it
  on the Runner after the person approves the exact statements. Today the Runner accepts these tasks only when the Workspace service starts them; that
  goes away.
- **Planned.** Agents in the console over MCP and built in, as the person. Today only the Gateway has an MCP endpoint, and the built-in agent in the
  console reads with the control service's own authority.
- **Planned.** `dbdatabuild`, `dbdatasync` and `dbdatastore` are removed; the Workspace and Runner services call Build, Sync and the Store as
  libraries.
- **Planned.** `ui mcp` is removed from every program.
- **Planned.** Screens that are still organised by service move to this layout.
- **Planned.** Administrators can turn on settings that block chosen risky actions, such as reading production while authoring. Today every
  risky action a person has access to is allowed with a warning.

## Decisions

| # | Decision | Why |
| --- | --- | --- |
| D1 | The console and the products are separate: servers with a management API and no UI; one shared console UI. | A product must keep working with the console down; one UI avoids three web apps. |
| D2 | One YAML format for every product (Gateway moved off JSON); no migration from `appsettings.json`. | One reader, one editor, one history; the clean break makes migration code unnecessary. |
| D3 | Each product keeps its own CLI; `dbdatakit` is the console's CLI only; lower-case executables; `<noun> <verb>` everywhere, old forms removed. | One grammar to learn; a console CLI that forwarded to products would hide which server a command touches. |
| D4 | Each product is a standalone install option; the suite release (console image and MSI included) comes from the same workflow and commit; separate MSIs; a service joins the console by the single-use command the console generates (or one console command for every service on one machine; `docs/install.md`). | Nothing forces a user to install products they do not use; one workflow keeps the cost low (`docs/releases.md`). |
| D5 | No self-updater runs beside a product (Sync's updater and launcher went with the Sync server). Images, MSIs, Scoop and the install scripts are the distribution; the console's upgrades (`DbDataKit.Upgrade`) reuse the `DbDataKit.Update`, `.Update.Service` and `.Update.Detect` libraries to download, verify and apply a release on a server. | One distribution route per platform; the update libraries' signed-manifest and verification code is reused, not written twice. |
| D6 | DuckDB 2.0.0 at once with the released engine pinned (`duckdb.pin`) and DuckDB.NET packages that carry no engine. Done. | Waiting for DuckDB.NET 2.0 would hold every product back. |
| D7 | No DuckDB 1.x after 2.0; code that existed only for 1.x is deleted. Done. | One engine to test. |
| D8 | Anything Build cannot reproduce exactly on DuckDB 2.0 is refused with a named diagnostic; `%` and `//` by zero follow 2.0 and the targets (they raise). Done. | A refusal is honest; a silent difference is a data bug. |
| D9 | Our own Roslyn scripting with NuGet libraries, embedded, no SDK at run time (`DbDataKit.Scripting`). | `dotnet script` and its embedded form both need `dotnet restore`, hence the SDK. |
| D10 | Secret store: encrypted values in a YAML file, key protected by DPAPI on Windows and a permission-restricted file elsewhere; no ClrKernel, nothing imported. | Works for services, CLIs and containers alike; an OS keychain is per user. |
| D11 | **Decided (owner, 2026-11-14): framework-dependent everywhere.** Every program of the suite needs the .NET 10 runtime (ASP.NET Core for the servers) installed on the machine, as the console, Runner, Sync, Store and Workspace already do; Build's single self-contained file and the Gateway's self-contained Windows build become framework-dependent like the rest (plan 33 SC25). **One exception:** the Gateway's 32-bit ODBC worker (`DbDataGateway.Worker`, win-x86) stays self-contained, so a machine with 32-bit drivers needs no second, 32-bit .NET runtime. | One prerequisite for every program, one publish mode in the scripts, smaller zips and runtime packages, and .NET's security fixes arrive through one runtime the machine updates (Microsoft Update on Windows, the package manager on Linux) instead of a rebuild of the suite. |
| D12 | **Revised (owner, 2026-11-14):** isolating Build's native SQL library (polyglot) in a child process is not required: no crash has been reported and the library is pinned and hash-checked. Existing child-process calls stay until a slice touches them; see "Our code calls our code as a library". | A defensive cost with no reported failure behind it. |
| D13 | A Build schedule lives in the project (committed with the refresh plan); an operator may pause or run now, not add. | Fits "everything is a file" and review of plans. |
| D14 | Trust: a client calls a server, never the reverse (a server will call the console once, to join it: `docs/install.md`); several clients per server, each with its own key and ceiling; short-lived signed tokens naming a person; ownership-scoped ceilings (Publisher) for server clients; the local CLI over a named pipe or Unix socket. | A server needs no credentials for any console and stays the security boundary (`DbDataKit.Web`). |
| D15 | Authoring merges by pushing to the project's git remote and opening a pull request when one is configured, else inside the Workspace server with the author's identity. | The usual review path where a remote exists. |
| D16 | The console's back end relays every browser call to the servers; browsers never reach them. | Servers stay off the browser's network, pinned self-signed certificates work, no CORS, and live updates (SignalR) are relayed. |
| D17 | A console and a server can differ in version: the management API carries an integer version, a client supports its own and the one before, and says which side to upgrade (since extended to the suite's compatibility generation, N and N-1). | Products release on their own. |
| D18 | The Gateway has a state store (SQLite through `DbDataKit.State`) for its audit, notifications and per-console read state. | It had none. |
| D19 | A Workspace project comes from a local path or a git remote and branch it clones; what runs is the package published from it, so a Runner never pulls a project. | Both ways of working exist; a run is reproducible from the published version. |
| D20 | One identity home per install, amended by plan 20: hosted by the serving layer by default. | End users must sign in without reaching the console; one list of people, roles assigned once. |

### Decisions recorded after plan 13

Decided by the owner after the table above was closed. The numbers are the ones the old follow-up list gave them (the plan is finished and deleted) and that source comments cite as `13c D<n>`; the details are in the README named in the last column.

| # | Decision | Why | Details |
| --- | --- | --- | --- |
| 13c D12 | Relationships between built models are written as `foreign_keys:` on the model; semantic relationships are read from them. | One spelling for a foreign key in both formats. Whether models and semantic models become one format is a proposal (plan 19 J30). | `docs/semantic/models-and-semantic-models.md` |
| 13c D22 | Installing a library adds `libraries/*/lib/` to the config repository's `.gitignore` (`LibraryPaths.EnsureStoreGitignored`). | The extracted packages are a local store; their DLLs must not be committed beside `library.lock`. | `DbDataSync/docs/drivers-and-libraries.md` |
| 13c D24 | A person's external (Entra, Windows) groups count until their next sign-in through that source: no expiry, and signing out does not clear them. | The home records what the last sign-in carried; a person removed from a group at the provider loses the role at the next sign-in. A lifetime can be added if a tenant needs one. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D25 | Entra's group overage (more than 200 groups, no `groups` claim) is left to the tenant: its administrator limits the groups with `groupMembershipClaims`; the home calls no Microsoft Graph. | Graph needs a credential and a permission of its own. Revisit if a tenant cannot limit its groups. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D26 | An Entra sign-in links to an existing account on the token's UPN (preferred) or email, trusted (`trustEmail`); other OpenID sources link only on a verified address. | An Entra id token has no `email_verified`; a tenant that lets people set their own mail attribute must not be trusted, so it is a setting. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D27 | Copying a connection to another server never copies a secret value: the copy names the reference the target must hold. | The console never reads a secret. | `DbDataKit/README.md` |
| 13c D28 | Sync's `extensions.sync` keys use the shared snake_case names (`segment_order`, `test_query`, `connect_timeout_seconds`, `command_timeout_seconds`). | One spelling for the keys every product reads from a `connection/1` file. | `DbDataSync/docs/configuration.md` |
| 13c D29 | The slow test engines' images (BigQuery emulator, sqlflite, SQL Server 2025, Spark) are pinned by digest in `testenv/docker-compose.yml`; the file says how to move one. | A moving tag can turn the nightly run red without a change of ours. | `testenv/docker-compose.yml` |
| 13c D30 | The identity home's admin API is the management API of whatever hosts the home; the console's screens use it through its relay and the commands stay. | One admin surface per host, no screens of the home's own. | `Shared/src/DbDataKit.Identity/README.md` |
| 13c D31 | A home that runs on its own keeps the framework key ring in its `dataprotection/` directory (a backup holds the state and that directory); the serving layer's home protects its signing keys with `KeySigningKeyProtector` and a key in its secret store. | Simple for one home; a group of homes on one shared state needs the same key everywhere. | `DbDataKit/README.md`, `Shared/src/DbDataKit.Identity/README.md` |

## Where the conventions live

| Convention | Documented in |
| --- | --- |
| Command grammar, effect classes, JSON output, exit codes, MCP | `Shared/src/DbDataKit.Cli/README.md` |
| Pairing, tokens, ceilings, roles, audit, version rules | `Shared/src/DbDataKit.Web/README.md`, `Shared/src/DbDataKit.Hosting.Local/README.md` |
| Browser relay, instances, connections across services | `DbDataKit/README.md` |
| Publishing, channels, bindings and signatures | `docs/publishing.md` |
| Identity home, OpenID Connect, tokens | `Shared/src/DbDataKit.Identity/README.md` |
| Schedules and the work queue; run model and health rules | `Shared/src/DbDataKit.Scheduling/README.md`, `Shared/src/DbDataKit.Monitoring/README.md` |
| Secret store | `Shared/src/DbDataKit.Secrets.Store/README.md`, `Shared/src/DbDataKit.Secrets.Cli/README.md` |
| Config layout, connection format | `Shared/src/DbDataKit.Config.Git/README.md`, `Shared/src/DbDataKit.Connections.Abstractions/README.md` |
| Scripting, libraries, drivers | `Shared/src/DbDataKit.Scripting/README.md` |
| Integrations: what they are, package kinds, authoring, build (csx with no SDK), test, publish; what is built and what is not | `docs/integrations.md` |
| Releases and distribution | `docs/releases.md`, `dist/README.md` |
| File formats, versions, upgrades | `docs/formats.md` |
| Security: the risks the design answers | `docs/security.md` |
