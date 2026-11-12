# The serving layer

Every endpoint that an end user, their tool or their app connects to is served by **one layer**, built in the DbDataGateway code base: **the serving layer (DbDataGateway; it was DbDataProxy until batch 17)**. The rename (plan 20 O14) was a scripted clean break: product, commands, packages, settings, secret names and screens, with the old names refused by a message (`docs/release-notes/dbdatagateway-rename.md`). Build's and Store's own words for "a server I publish to" (`server proxy`, `proxies.json`) follow in O14b.

Why one layer, in the owner's words:

- **One layer to firewall** in a simple install. End users reach only the serving layer; the back-end servers and the console stay private.
- **One layer to scale out.** A deployment chooses which endpoints each instance hosts.
- **High availability while the back-ends are patched.** Reads, writes of managed data, sign-in and apps keep working when Sync, Build or DbDataStore's server is down.
- **Simpler patching for the back-ends.** The systems that change definitions can take a short outage, because nothing an end user does at runtime depends on them.

## Three layers

| Layer | What it is | Who reaches it |
| --- | --- | --- |
| **Serving layer** | every end-user endpoint, as modules; runs from published definitions; reads and writes the data stores directly | end users, their tools, apps, agents; behind a load balancer when scaled out |
| **Back-end servers** (DbDataSync, DbDataBuild, DbDataStore's server, DbDataRunner) | definitions and data movement: replications, models and loads, master data models and structure changes, scheduled work; they publish what the serving layer needs | the console, and the serving layer only for what the outage table names |
| **Control plane** (the console) | administration through the management APIs | administrators, on the private side |

A console calls servers and a server never calls a console. The serving layer is one more paired server (a *serving group*).

## Principles

1. **It runs from published definitions.** Back-ends publish versioned definitions to the serving group: semantic packages and their data sources from Build (`semantic-model/<name>`, `data-source/<name>`), entity models from DbDataStore (`entity-model/<name>`), app versions and instances (`app/<name>`, `app-instance/<name>`). They live in the group's `DefinitionStore` (`DbDataKit.State`), keyed by channel and version, each recording what it depends on. Each instance follows the store (`DefinitionFollower`, polled every `state.definitionPollSeconds`, 5 by default), builds a new version before it replaces the one served, switches atomically and keeps the last good version; a version an instance cannot load is reported and leaves the old one serving. Nothing at runtime calls a back-end, so an offline back-end only delays the next publish.
2. **Data paths are direct.** Reads go straight to the databases with read logins. DbDataStore's reads and writes happen in the serving layer, through the `DbDataKit.Store` write path against the model's tables; nothing is forwarded to DbDataStore's server, and a published model is refused by the store server's own data API (one data path per model).
3. **The logins stay split by job.** Build's write login for its targets never leaves Build. DbDataStore's data write login lives in the serving layer, kept as the group's secret encrypted with the group key. DbDataStore's structure login (DDL, `DbDataKit.Schema`) stays in its server.
4. **Modules, enabled per instance.** One process can host every module, or each instance a few. `modules:` in `server.yaml` lists them (`flightsql`, the default, `identity`, `apps`, `store`); an unknown or repeated name is refused naming the file; an instance without `flightsql` opens no Flight port.
5. **Untrusted or heavy work runs in child processes**: app code, the semantic combine, extraction from documents. A crash or a limit stops that one job, not the instance. (Today the combine runs in process under limits; see plan 20.)

## Modules

| Module | Serves | Status and where the code is |
| --- | --- | --- |
| `flightsql` | read-only data sources over Flight SQL | built: `DbDataKit.Serving.FlightSql` |
| semantic | semantic queries and semantic SQL on the source `semantic:<model>` | built, not a listed module: `DbDataKit.Serving.Semantic` wraps the host's core services, configured by `semantic:` in `server.yaml` (see `docs/semantic/architecture.md`) |
| `store` | DbDataStore entities over HTTP: reads now, as of a date, with history; validated writes; change requests in the group's state | built over HTTP (`GatewayStoreModule`, its own listener, the paths of a DbDataStore server); reads over Flight SQL not built |
| `apps` | app front ends and their API: subpath instances, shell page, app tokens (the identity home's, by exchange), app storage, portals, data calls answered in process | built (`GatewayAppsModule`, `DbDataKit.Apps.*`, default port 8818); app code does not run in child processes |
| `identity` | sign-in, the OpenID Connect provider (authorize, token, discovery, signing keys), passkeys, Entra and Windows upstream sign-in | built (`GatewayIdentityModule`, `DbDataKit.Identity`, default port 8817); a hosted web application that reports its health as an `IServingModule` |
| `views` | published dashboards and reports over REST, rendered as the caller, audited, with drain and health (plan 32 DBR11); and a person's own dashboards and reports (`/api/v1/my/...`, state module `user_views`, DBR12) | built as a library with its own listener: `DbDataKit.Views.Hosting` (`ViewsModule`); **not yet a name in `modules:`**, the Gateway host does not load it (it needs a listener setting and an `IViewsCallers` over the identity home's tokens), see `docs/views.md` |
| `pgwire` | Postgres wire for semantic SQL | not built; deferred, low priority (owner, 2026-11-09; plan 14a) |
| `xmla` | XMLA and MDX for Excel PivotTables | not built (only the recording forwarder, `DbDataKit.Tools.XmlaForwarder`); deferred, low priority (owner, 2026-11-09; plan 14a) |
| `feeds` | saved queries and semantic requests over REST (JSON, CSV, Arrow IPC), for report servers (`docs/bi-tools-on-semantic-models.md`), Power Query and scripts | built (plan 14a SL31): `modules: [feeds]` and the `rest:` section (address, port 8821, `defaultLimit`, `maxRows`), a listener of its own with the Gateway's certificate; `GET|POST /api/v1/queries/{model}/{name}` and `POST /api/v1/query`; JSON, CSV or Arrow IPC (`?format=` or `Accept`); the home's personal access, access and Basic tokens; paging is `limit` only; XML for SSRS and conditional requests are not built |
| `webdav` | DbDataStore documents, with extraction on save | not built |
| `mcp` | the agents' MCP endpoint, acting as the signed-in user with approvals | built (plan 20 O12, `DbDataGateway`, port 8822): see "The mcp module" below. The Gateway's `ui mcp` noun is a different thing: the command surface offered to agents by the console and Kit |
| `knowledge` | search, show and about over knowledge items, access-filtered; checks and embeds a knowledge project at staging and switches at activation (plan 27 sections 2.2, 2.6) | not built (plan 27a lane KN); no knowledge library, index or embedding connection exists, only the engine check for vector columns, see [knowledge.md](knowledge.md) |
| `backlog` | the API of DbDataBacklog: work items, sprints, boards, over its own state module (plan 27 section 5.1) | not built (plan 27a lane BK: BK02); its state module `backlog` and library `DbDataKit.Backlog` are built (BK01) |

Health per module is on the host's `/health` and `/health/ready`: each of `flightsql`, `identity`, `apps`, `store` and `views` is one component named after the module (`ServingModuleReadinessProbe`). A module that is not started is `Unhealthy`, one that drains is `Degraded`, and a group state database that does not answer turns `identity`, `apps` and `store` to `Unhealthy` and `flightsql` to `Degraded` (it still reads the hand-made sources); `views` reports its own causes (a version it cannot load) too.

## What keeps working when a back-end is down

| Down | Keeps working | Waits |
| --- | --- | --- |
| DbDataBuild | every query, including semantic queries, on the last published packages | new or changed packages, refreshes, authoring |
| DbDataSync | every query, on data as of the last replication | replication; its data gets older (monitoring shows freshness) |
| DbDataStore's server | entity reads and writes, change requests | model and structure changes, new rules or permissions |
| The console | everything an end user does | administration, approvals made in the console, monitoring views |
| A serving instance | the others behind the load balancer | nothing, if the group has more than one instance |

Tests of the rows with the back-end not running: `OutageTests` (Build, Sync and the console, on a serving instance of its own with nothing beside it) and `GroupStateTests` (the DbDataStore server, on SQL Server and PostgreSQL). The row for a stopped serving instance waits for the high-availability tests (plan 20 O05).

## High availability, groups and identity

- **Shared state.** Instances of a group share their state on SQL Server or PostgreSQL through `DbDataKit.State`. It holds sessions (a pass-through session's credentials encrypted with the group key, the secret `dbdatakit:gateway:state:session-key`, which every instance must hold), the identity home's tables, one-time values (passkey challenges, Entra attempts, used management tokens), audit, change requests, app storage, the published definitions and each instance's lease. SQLite is for a single instance, and a second instance against SQLite is refused with a message.
- **A serving group is one server.** Its instance id, paired clients and pairing codes are in the state, so a console pairs once through any instance with a code made on any other, and every instance presents the same server id and (when self-signed) one certificate kept in the state. Build and DbDataStore pair with a group as a publishing client; the data sources a client publishes are the group's, served by every instance beside the hand-made ones, a hand-made name winning.
- **Identity is a module of the serving layer by default**, so end users can sign in without reaching the private console. Managing users, groups and roles stays an administrator's job through the management API. Every console becomes an OpenID Connect client of the home. The signing keys are shared by the group's instances (`dbdatakit:gateway:identity:signing-keys`; an instance with another key refuses to start). The home can instead run on its own behind the firewall (`dbdatakit identity run`), which keeps the keys and the user store off the edge.
- **Load balancing is external.** Instances are interchangeable, and Flight SQL tickets and sessions are valid on any instance of the group. NTLM needs a balancer that keeps a connection on one instance.
- **Lineage and monitoring**: each instance records its runs into the group's state; the console pulls them per group.

## The result cache

The core keeps a **result cache** in front of the query service (plan 24a UC12 to UC14 and UC16; `ServingCache.cs` in `DbDataKit.Serving.Abstractions`, wired by the Gateway as `GatewayCache`). It is **off for every definition that does not turn it on**: an owner writes a `cache:` section in the definition (a `connection/2` data source or the semantic package; the keys `enabled`, `ttl`, `scope`, `dataset` of [plan 24a UC03](plans/24a-upgrade-and-cache-slices.md)), and publishes it, so the setting is promoted with the definition. Dashboards, reports and apps read through the same query service, so they take the cache setting of what they read; they have no `cache:` key of their own.

What is cached, and what never is:

| | |
| --- | --- |
| Cached | a statement over data on a data source whose definition says `enabled: true`; a semantic query (`kind: query`, or semantic SQL beginning with `SELECT` or `WITH`) on a model whose catalog says so |
| Never cached | a preview, MDX, a description of the catalog, a member listing, `EXPLAIN`, a call of a saved query; a **pass-through** data source (its rows are the caller's own database login's); a data source that **accepts writes** (a write changes it without a message); a model that reads Analysis Services or a data source of those kinds; an answer that is too big for an entry, that fails or passes a limit while it streams, or that was read before a clear |
| Granted | before a request is answered from the cache the Gateway asks, as for a miss, whether the **caller may use every data source** the request reads; an entry another caller made is never a way round a grant |

The key is one SHA-256 over the query as asked (kind, source, text or document, parameters, the limits asked for), the channel, the preview, the published version of the definition (for a data source: a hash of its file's settings, so a republish or a changed login is a new key) and, by `scope`, who the entry is for: `caller` (the caller's id), `rules` (all of the caller's directory attributes, **the default taken until the access-rule language exists**, plan 22a QG2), or `shared` (no one). A hit says the same version and channel and adds `cache: hit` to the provenance. A cold or lost cache changes no result.

**What clears it.** (1) The time-to-live of the definition (`ttl`, 5 minutes by default, at most 24 hours): the backstop for a source written outside the suite. (2) A **`data.changed` message** (`data.changed/1`, `DataChangedMessage`): `POST /api/v1/cache/changed` on the Gateway's management API, with the role Publisher or Admin, a JSON object naming `dataset` (the lineage identity, `namespace/name`), `product` (`sync`, `build`, `store`, `runner`) and optionally `run`, `at` and `only_if_served`; any other key is refused by name, so no row data and no text travels in it. With `only_if_served: true` (what the producers send, because they cannot know what a group serves) a group whose lineage graph does not know the dataset **does nothing** and answers `served: false`; the screen counts those as *ignored*, apart from the changes that cleared something. The call is a signed management call, so a forged or replayed message is refused by the channel before it is read. The Gateway maps the dataset to what it serves with its lineage graph (`Impact` of the table reaches the data sources that serve it and the models that read them) and clears those entries; **a dataset the graph does not know clears the whole cache** (a cold cache is never wrong, a stale one can be). (3) An administrator's **clear** on the console's cache screen, per dataset or all. (4) A new published version is a new key, so the old entries are never found again and leave when they expire. A write that is in flight while the clear happens keeps its answer out of the cache (the epoch of the cache).

**Groups.** Each instance keeps its cache in memory. With a group's state (SQL Server or PostgreSQL) the instances share their clears through the state module `serving_cache_epoch`: one row per cache dataset holding a number, its epoch, that only grows, and who and why. **No row data and no result is ever stored in the state.** Every instance reads the epochs every two seconds and clears what it has not seen, so a message that reached one instance reaches them all; with the shared record down an instance still clears itself, and the others fall back to the time-to-live (a miss, never an error). A single instance on SQLite does not use the module.

**What the console shows** (the Gateway's *Cache* screen, `GET /api/v1/cache`): per instance the hits, misses, hit rate, entries, bytes and evictions; the same by dataset; the cache setting of every data source and semantic model (off unless the definition says so, and the reason an enabled one does nothing); and what the cache has heard of data changes (how many, the last, from which product, whether the group shares them), so "events not arriving" is visible and a long time-to-live is not a silent staleness.

**The producers** are in the library `DbDataKit.Serving.Notify` (`DataChangeNotifier`, README beside the code): a product server that finished a dataset sends the message to every serving group it is paired with. A completion has no person, so **the server signs as itself**: the management token comes from the key of the server's own pairing with the Gateway (the one it publishes with), names the server's client id and `<product> server`, with the existing Publisher role and no second signature (the channel's replay cache applies). Delivery is in the background and **never fails the run**: a failure that can pass (Gateway down, timeout, 5xx) is tried once more after 30 seconds, a refusal is not; each is a log warning and a counter. A product with no pairing sends nothing. Dataset names are the lineage identities the product already gives its tables (`LineageIdentity.Dataset`), so they are the names the Gateway's graph holds.

Known limits: a hit is not recorded as a semantic run (SL23: the run record is written by the module a miss goes through); the sizes (64 MB, 8 MB an entry) and the two-second poll are not settings yet.

## Decisions

1. The `apps` and `mcp` modules are part of the serving layer.
2. Extraction on save runs in the serving layer, where the document arrives; the extraction definitions come from DbDataStore's server.
3. Approving a data change request is a runtime action in the serving layer, like the write; approving a structure change stays in DbDataStore's server.
4. **The rename is DbDataGateway**, done by `scripts/rename_proxy_to_gateway.py` in batch 17 (plan 20 O14) as a clean break.
5. Publishing is the one act that makes a model available (plan 20 O10b): a model being worked on reaches no group until it is published.
6. **Who may read a published data source is the published definition's `grants` section** (owner, 2026-11-08; `connection/2`, authored in Build's connection file). A republish replaces it completely; the Gateway adds none, enforces what the definition says, and its data source screen shows the grants read-only. A publish that contradicts the Gateway's own settings is not rejected (deferred on purpose).
7. **The same definition carries the Gateway's serving policy** (owner, 2026-11-09): `extensions.gateway` (`pass_through`, `bitness`, `classifier`, `metadata`, the limits, `batch_size`, `compression`), replaced whole by a republish. `allow_writes` and `allowed_directories` are not part of a definition: a publish that carries either is refused, and editing a published data source on the Gateway is not supported. A publisher adopts a data source made by hand with `adopt: true`; the replaced file goes to the history and the answer lists what changed or was dropped. The Publisher role is high-trust.

## Known limits of what is built

What a reader of the code should not assume; each is either a deliberate limit or owned by a slice of [plan 20](plans/20-serving-layer.md).

- **Modules** (O01): the identity home, the apps host and the store module implement `IServingModule` for its name, drain and health but start themselves as hosted services on their own listeners; `StartAsync(IServingServices)` is their ordinary start, they are not handed the core services.
- **Identity keys** (O02b): `dbdatakit identity run` protects its signing keys with ASP.NET data protection keys in its own directory, so a group of those homes needs the folder copied (O06b). Entra's group overage (more than 200 groups) gives no groups; Windows on Linux with a keytab is built but untested, there being no KDC in the test environment; `identity run` does not offer the upstreams yet (O06b).
- **Definitions** (O03, O19): a semantic request is pinned to the model version it found when it began and its answer names that version (`ServingProvenance.DefinitionVersion`); a raw SQL request on a published data source does not name a version yet. `expected_version` is checked in the shared state: two publishes at once from the same version leave one winner, and the other gets a `DefinitionConflictException` naming the current version (the unique key on the version number is the guard; tests on SQLite, SQL Server and PostgreSQL).
- **Groups** (O04): Build, Sync and Store run once, they are not groups. Adopting a hand-made data source on a group keeps it the instance's own file. The console shows a group's data sources as the instance it reaches lists them (O20).
- **Store publishing** (O10, O10b): permissions are not in `model/1` (row and column rules are plan 22a's access rules); publishing is a command, not the Store's management API or screens, and a model with instances cannot be published yet (the group serves a model's own copy only) (O10c).
- **Semantic** (plan 14a): the combine runs in process under limits, not in a child process (SL27, conditional); semantic state is not in the Gateway's store (SL23, SL25).

## The contract

`DbDataKit.Serving.Abstractions` holds it. `IServingHost` is the small host a fake or a test can implement; the services below are what a module takes.

### Decisions

1. **Libraries plus a thin host.** Each module's protocol code is a `DbDataKit.Serving.*` library with no reference to Gateway. The Gateway host loads modules, configures them from `server.yaml` and supplies the core services.
2. **The core owns the duties; adapters are thin.** An adapter reads credentials and speaks its wire format. The core authenticates, holds sessions, applies access rules and limits, audits, caches and traces. A module is never handed a raw executor.
3. **Services per capability, not one request union.** A module takes only the services it needs.
4. **Sessions are core sessions in shared state**, valid on any instance. Protocol-specific state (a Postgres prepared statement, an XMLA session) is an opaque blob the adapter owns, with a time-to-live.
5. **A query result is an Arrow stream plus provenance**: the schema, counts, the canonical query that ran, the definition version and the channel. Adapters convert it (Postgres rows, an XMLA cell set, JSON).
6. **Access rules (plan 22) are applied inside the core**, on the plan: the caller's attributes are resolved, the row filter is added before execution, columns are hidden or masked in the result; adapters only see filtered data; any cache key includes the attribute values. (Not built; the contract leaves the place.)
7. **One rich catalog model.** The core returns the objects a caller may see (datasets with columns, types, keys and descriptions; for semantic models, dimensions, hierarchies and measures; for entities, as-of support) and each adapter renders its own catalog views from it.

### The module contract

`IServingModule`: a `Name` (the one used in `server.yaml`), `StartAsync(services)`, `DrainAsync` (stop taking new work and finish running work, so an instance can be upgraded), `StopAsync` and `Health()` (`Ok`, `Degraded`, `Down`). A module takes the services it needs from `IServingServices`; a service the host does not offer is null.

### Core services

| Service | What a module can ask | Used by |
| --- | --- | --- |
| Identity (`IServingIdentity`) | authenticate credentials; resolve the caller's roles | every module |
| Sessions (`IServingSessions`) | create, resume and close a session; hold the adapter's opaque state | `flightsql`, `pgwire`, `xmla`, `mcp` |
| Query (`IServingQueries`) | run a typed request (SQL pass-through, semantic request, semantic SQL, later MDX) under the caller's limits, channel and draft; cancel | `flightsql`, semantic, `pgwire`, `xmla`, `feeds`, `apps` |
| Catalog (`IServingCatalog`) | describe what the caller may see; server facts | the protocol modules |
| Entities (`IServingEntities`) | as-of reads, validated writes (a row change or a change request, never a table write) | `store`, `apps`, `mcp` |
| Updates (`IServingUpdates`) | statements that change a writable source | `flightsql` |
| Documents, Definitions, Platform | versioned documents and extraction; the current published version per channel with a request pinned while it runs; audit, limits, the result cache, OpenTelemetry, child processes | not in the code yet (published definitions are read through `DefinitionStore`) |

`identity`, `apps` and `mcp` use Identity and Platform and call Query, Entities and Documents in process, as the signed-in user.

### The caller context

Every call carries one `ServingContext`: the caller (identity and roles), the channel (`prod` until channels are built, plan 21) and any preview, the session, a trace id and the client address (for the login throttle and audit). Services take it as their first argument, so no module can leave it out.

### What each module needs

| Module | What it needs from the core | In the contract today | Added when it starts |
| --- | --- | --- | --- |
| `flightsql` | a handshake that trades basic credentials for a token; calls authenticated from a bearer token or basic credentials, with a login throttle keyed on user and client address; SQL with positional typed parameters (prepared statements keep their handle and bound rows as adapter state); updates to writable sources; schemas, tables, columns, primary keys; server facts; a classified database failure mapped to a gRPC status with a fixed sentence; compression per source | all of it: `ServingParameter`, the client address, `IServingUpdates`, `ServingObject.PrimaryKey`, `FactsAsync`, `ServingBackendException` carrying the shared `ConnectionFailure` | built |
| semantic | semantic request, semantic SQL, the catalog of models, an explain that returns the plan without running it | requests, catalog kinds | explain is a statement on the model source, not a service method |
| `pgwire` | a multi-step password exchange (SCRAM), portals and cursors (adapter state), a cancel key per connection, `COPY` as an ingest, `pg_catalog` views from the catalog | sessions, queries, catalog | a multi-leg authentication exchange on the identity service (`ServingCredentials` cannot express a challenge), cancel by session, ingest |
| `xmla` | `Discover` rowsets from the catalog (cubes, dimensions, hierarchies, levels, measures, members, properties, perspectives), `Execute` of MDX into a cell set, sessions | MDX request, catalog kinds `Dimension`, `Hierarchy`, `Level`, `Measure`, sessions | member listing and perspectives in the catalog; a convention for the axes of a cell set in the Arrow schema's metadata |
| `feeds` | paged results with a stable identity for conditional requests, a refresh from a watermark | queries and provenance | a data version in the answer for an ETag, from the platform's result cache |
| `store` | as-of reads, writes that insert, change or delete a row against an expected row version, change requests, history of a row | `IServingEntities.ReadAsync`, `WriteAsync(rows)` | `WriteAsync` becomes a change set (insert, upsert, delete; expected version) answering with the rows applied or the change request made; the HTTP module (`GroupStoreApi`) does this today without the service |
| `webdav` | versioned documents, locks, properties, extraction on save | none | the documents service |
| `apps` | acts as the signed-in user, queries, entities and documents in process | identity (a bearer token from the identity module), queries, entities | documents, when apps need them |
| `mcp` | tools that act as the signed-in user, with the user approving each change as it happens | identity, queries, entities | an approval request on the platform service (ask the signed-in user, wait for the answer): built as the `agents` state module's approval store, in the group's state (see "The mcp module") |
| `identity` | a producer, not a consumer: it issues tokens and holds the sign-in; the core's identity service trusts the issuer it names | identity validates bearer tokens | no change: the module registers its issuer with the core |

Two decisions the review made because the other choice is dearer to undo: a **positional** parameter list, not a dictionary, because Flight SQL and the Postgres wire both bind by position; and the **classified failure is the shared `ConnectionFailure`**, not a new type, so the message a client sees stays word for word what it is.

## The mcp module

Decided by the owner's delegation ("take your best pick", 2026-11-12; the plan row cited plan 16, which no longer exists). **Default taken**: the module is a serving module (`IServingModule`, a line named `mcp` on `/health`) of the Gateway that speaks MCP over streamable HTTP, on a listener of its own (`mcp: { address, port: 8822, approvalMinutes: 60 }`, https with the Gateway's certificate, listed in `modules:`).

* **Endpoint.** `POST /mcp`, JSON-RPC (`initialize`, `ping`, `tools/list`, `tools/call`; a batch is answered as a batch; a notification gets 202). Each answer is one JSON document: no stream is opened and no session is kept, so any instance of a group answers any call (`GET /mcp` is 405). The caller is a user of the identity home, by the same tokens as the store module (a service account's or a person's personal access token, or an access token the home issued); without one the answer is 401.
* **Read tools run at once, as the caller.** `list_semantic_models` (only the models whose catalog the caller may read), `describe_semantic_model`, `query_semantic` (the semantic request, through the same serving services as Flight SQL, so the caller's home groups decide which data sources it may use), `list_views` (names and served versions; needs the viewer role), `list_entity_models`, `describe_entity_model`, `read_entity_rows`, and `approval_status`.
* **Dashboards and reports** (plan 32 DBR20): `views_validate` reads at once (a document is checked as a person's file is, against the models the caller may read, no explicit `sql`); `dashboard_draft`, `report_draft`, `layout_add` and `view_pin` are tools with an effect: the draft is checked when asked for (a draft that would be refused is never put to a person) and saved, on approval, as a version of the caller's own user document (a service account's, for an agent's token). Tools: `McpViewTools`.
* **A tool with an effect never runs when an agent calls it.** `write_entity_changes` (a batch of changes to a published master data model) is the one today. The call is refused at once when the caller could not do it anyway (no author role); otherwise it is recorded as an approval request (who asked, tool, arguments, a sentence about it) and the agent is answered `pending approval <id>`. It asks `approval_status` with the id: waiting, approved (with what the run gave, or why it did not run), denied or expired. A new tool with an effect is a `McpTool` with `Effect: true`: nothing else is needed for it to wait.
* **A person answers it, on the module.** `GET /api/v1/mcp/approvals[?state=all]` lists what waits and `POST /api/v1/mcp/approvals/{id}/approve` or `/deny` (`{"note": "..."}`) answers. The one who answers is a person who signed in (an access token the identity home issued; a personal access token or a service account's token is refused with a 403 saying why, so an agent's own token never answers), is not the one who asked, and holds the approver or admin role on this server (403 otherwise). Approving runs the tool once, as the requester with their roles as they are then, and answers with what it gave; the first answer stands (409 for a second one or one after the end). A denied or expired request never runs.
* **Approvals expire** after `approvalMinutes` (a request nobody answered in time is expired when it is next looked at, and an answer to it is 409).
* **The store is AG15's**: the `agents` state module's `approval` and `audit_event` tables (origin `mcp`), in the group's state (SQLite, SQL Server or PostgreSQL), written through `AgentStore.RequestApproval`, `Answer` and `RecordAudit`; no second approval store. Every request, answer and run is an audit row naming the person, the agent (the service account's name) and the step.
* **Limits.** A step is at-most-once: when the instance stops between the answer and the run, the step is recorded as approved and was not run, and the agent asks again. `list_views` does not filter by a view's own grants. The approvals have no screen yet (`docs/ui-editors.md`, the agents row); until it exists a person answers with the routes above.

