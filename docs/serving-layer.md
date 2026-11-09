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
| `flightsql` | read-only data sources over Flight SQL | built: `DbDataKit.Serving.FlightSql`, the only `IServingModule` implementation |
| semantic | semantic queries and semantic SQL on the source `semantic:<model>` | built, not a listed module: `DbDataKit.Serving.Semantic` wraps the host's core services, configured by `semantic:` in `server.yaml` (see `docs/semantic/architecture.md`) |
| `store` | DbDataStore entities over HTTP: reads now, as of a date, with history; validated writes; change requests in the group's state | built over HTTP (`GatewayStoreModule`, its own listener, the paths of a DbDataStore server); reads over Flight SQL not built |
| `apps` | app front ends and their API: subpath instances, shell page, token broker, app storage, portals, data calls answered in process | built (`GatewayAppsModule`, `DbDataKit.Apps.*`, default port 8818); app code does not run in child processes |
| `identity` | sign-in, the OpenID Connect provider (authorize, token, discovery, signing keys), passkeys, Entra and Windows upstream sign-in | built (`GatewayIdentityModule`, `DbDataKit.Identity`, default port 8817); a hosted web application rather than an `IServingModule` |
| `views` | published dashboards and reports over REST, rendered as the caller, audited, with drain and health (plan 32 DBR11); and a person's own dashboards and reports (`/api/v1/my/...`, state module `user_views`, DBR12) | built as a library with its own listener: `DbDataKit.Views.Hosting` (`ViewsModule`); **not yet a name in `modules:`**, the Gateway host does not load it (it needs a listener setting and an `IViewsCallers` over the identity home's tokens), see `docs/views.md` |
| `pgwire` | Postgres wire for semantic SQL | not built; deferred, low priority (owner, 2026-11-09; plan 14a) |
| `xmla` | XMLA and MDX for Excel PivotTables | not built (only the recording forwarder, `DbDataKit.Tools.XmlaForwarder`); deferred, low priority (owner, 2026-11-09; plan 14a) |
| `feeds` | result feeds for report servers and tools | not built |
| `webdav` | DbDataStore documents, with extraction on save | not built |
| `mcp` | the agents' MCP endpoint, acting as the signed-in user with approvals | not built as a module (the Gateway's `ui mcp` offers the `semantic` noun to agents) |
| `knowledge` | search, show and about over knowledge items, access-filtered; checks and embeds a knowledge project at staging and switches at activation (plan 27 sections 2.2, 2.6) | not built (plan 27a lane KN); no knowledge library, index or embedding connection exists, only the engine check for vector columns, see [knowledge.md](knowledge.md) |
| `backlog` | the API of DbDataBacklog: work items, sprints, boards, over its own state module (plan 27 section 5.1) | not built (plan 27a lane BK) |

Health per module is not wired into the host's `/health`: `IServingModule.Health()` exists; the `views` module reports its own at its listener's `/health`.

## What keeps working when a back-end is down

| Down | Keeps working | Waits |
| --- | --- | --- |
| DbDataBuild | every query, including semantic queries, on the last published packages | new or changed packages, refreshes, authoring |
| DbDataSync | every query, on data as of the last replication | replication; its data gets older (monitoring shows freshness) |
| DbDataStore's server | entity reads and writes, change requests | model and structure changes, new rules or permissions |
| The console | everything an end user does | administration, approvals made in the console, monitoring views |
| A serving instance | the others behind the load balancer | nothing, if the group has more than one instance |

Tests for each row with the back-end stopped are in plan 20 (O13); several of the rows are exercised by `GroupStateTests`, which run a done-when with no Store server or no Build.

## High availability, groups and identity

- **Shared state.** Instances of a group share their state on SQL Server or PostgreSQL through `DbDataKit.State`. It holds sessions (a pass-through session's credentials encrypted with the group key, the secret `dbdata:gateway:state:session-key`, which every instance must hold), the identity home's tables, one-time values (passkey challenges, Entra attempts, used management tokens), audit, change requests, app storage, the published definitions and each instance's lease. SQLite is for a single instance, and a second instance against SQLite is refused with a message.
- **A serving group is one server.** Its instance id, paired clients and pairing codes are in the state, so a console pairs once through any instance with a code made on any other, and every instance presents the same server id and (when self-signed) one certificate kept in the state. Build and DbDataStore pair with a group as a publishing client; the data sources a client publishes are the group's, served by every instance beside the hand-made ones, a hand-made name winning.
- **Identity is a module of the serving layer by default**, so end users can sign in without reaching the private console. Managing users, groups and roles stays an administrator's job through the management API. Every console becomes an OpenID Connect client of the home. The signing keys are shared by the group's instances (`dbdata:gateway:identity:signing-keys`; an instance with another key refuses to start). The home can instead run on its own behind the firewall (`dbdatakit identity run`), which keeps the keys and the user store off the edge.
- **Load balancing is external.** Instances are interchangeable, and Flight SQL tickets and sessions are valid on any instance of the group. NTLM needs a balancer that keeps a connection on one instance.
- **Lineage and monitoring**: each instance records its runs into the group's state; the console pulls them per group.

## Decisions

1. The `apps` and `mcp` modules are part of the serving layer.
2. Extraction on save runs in the serving layer, where the document arrives; the extraction definitions come from DbDataStore's server.
3. Approving a data change request is a runtime action in the serving layer, like the write; approving a structure change stays in DbDataStore's server.
4. **The rename is DbDataGateway**, done by `scripts/rename_proxy_to_gateway.py` in batch 17 (plan 20 O14) as a clean break.
5. Publishing is the one act that makes a model available (plan 20 O10b): a model being worked on reaches no group until it is published.

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
| `mcp` | tools that act as the signed-in user, with the user approving each change as it happens | identity, queries, entities | an approval request on the platform service (ask the signed-in user, wait for the answer) |
| `identity` | a producer, not a consumer: it issues tokens and holds the sign-in; the core's identity service trusts the issuer it names | identity validates bearer tokens | no change: the module registers its issuer with the core |

Two decisions the review made because the other choice is dearer to undo: a **positional** parameter list, not a dictionary, because Flight SQL and the Postgres wire both bind by position; and the **classified failure is the shared `ConnectionFailure`**, not a new type, so the message a client sees stays word for word what it is.
