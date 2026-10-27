# Plan 20: one serving layer for every end-user endpoint

Status: **decided by the owner (2026-10-26)**, including the details section 8 lists. This plan
changes parts of plans 13, 14, 15, 16, 17 and 19; section 7 lists each change.

## 1. The decision

Every endpoint that an end user, their tool or their app connects to is served by **one layer**, built in **the DbDataProxy code base**.
It may be renamed later (DbDataServer, DbDataHost or similar). The owner's reasons:

- **One layer to firewall** in a simple install. End users reach only the serving layer; the back-end servers and the console stay
  private.
- **One layer to scale out**, from an application standpoint. A deployment chooses which endpoints each instance hosts.
- **High availability while the back-ends are patched.** Reads, writes of managed data, sign-in and apps keep working when Sync, Build
  or DbDataStore's server is down.
- **Simpler patching for the back-ends.** The systems that change definitions (Build, DbDataStore's model management, Sync) can take a
  short outage, because nothing an end user does at runtime depends on them.

## 2. Three layers

| Layer | What it is | Who reaches it |
| --- | --- | --- |
| **Serving layer** (DbDataProxy, later renamed) | every end-user endpoint, as modules (section 4); runs from published definitions; reads and writes the data stores directly | end users, their tools, apps, agents; behind a load balancer when scaled out |
| **Back-end servers** (DbDataSync, DbDataBuild, DbDataStore's server) | definitions and data movement: replications, models and loads, master data models and structure changes; they publish what the serving layer needs | the console, and the serving layer only for what section 5 names |
| **Control plane** (the console) | administration through the management APIs (plan 13 W12) | administrators, on the private side |

The rule of plan 13 W12 still holds: a console calls servers, and a server never calls a console. The serving layer is one more paired
server (a *serving group*, section 6).

## 3. Principles

1. **The serving layer runs from published definitions.** Back-ends publish versioned definitions to the serving group:
   - semantic packages and their data sources, from Build;
   - entity models, rules and permissions, from DbDataStore;
   - app versions, for DbDataApps.

   Each instance switches to a new version atomically and keeps the last good version. Nothing at runtime calls a back-end, so a back-end
   that is offline only delays the *next* publish. This generalizes plan 14 R1 (hosting on Proxy) to every kind of definition.
2. **Data paths are direct.**
   - Reads go straight to the databases with read logins.
   - **DbDataStore's reads and writes happen in the serving layer** (owner, 2026-10-26). The write path is the `DbDataKit.Store.*`
     libraries (validation, rules, history, audit, change requests), hosted in the serving layer. They write DbDataStore's database
     directly; nothing is forwarded to DbDataStore's server.
3. **The logins stay split by job.**
   - Build's write login for its targets never leaves Build (plan 14 R2a, unchanged).
   - DbDataStore's *data* write login lives in the serving layer, which is the runtime writer.
   - DbDataStore's *structure* login (DDL, `DbDataKit.Schema`) stays in DbDataStore's server.
4. **Modules, enabled per instance.** One process can host every module (a simple install), or each instance can host a few (scale
   out). This is configuration and deployment, not a code path. Each module reports its own health.
5. **Untrusted or heavy work runs in child processes:** app code, the semantic combine across sources (plan 14), and extraction from
   documents. A crash or a limit stops that one job, not the instance.

## 4. Modules

| Module | Serves | Comes from |
| --- | --- | --- |
| `flightsql` | read-only data sources over Flight SQL | Proxy today |
| `semantic` | semantic queries, semantic SQL, joins across sources (plan 14) | plan 14 Q5, Q11; plan 19 J3 |
| `pgwire` | Postgres wire (plan 14 R8, plan 17 R4) | later |
| `xmla` | XMLA and MDX for Excel (plan 14 R7) | later |
| `feeds` | result feeds for report servers and tools (plan 17 R2) | later |
| `store` | DbDataStore entities: reads (as of a date), validated writes, change requests, over Flight SQL and an HTTP API | plan 15, moved here |
| `webdav` | DbDataStore documents, with extraction on save | plan 15 section 6, moved here |
| `apps` | the DbDataApps host: app front ends and their API | plan 16 section 2, moved here |
| `mcp` | the agents' MCP endpoint, acting as the signed-in user with approvals | plan 16 section 1 |
| `identity` | sign-in, the OpenID Connect provider (authorize, token, discovery, signing keys), Windows and Entra federation | plan 16 section 2b, moved here (section 6) |

## 5. What keeps working when a back-end is down

| Down | Keeps working | Waits |
| --- | --- | --- |
| DbDataBuild | every query, including semantic queries, on the last published packages | new or changed packages, refreshes, authoring |
| DbDataSync | every query, on data as of the last replication | replication; its data gets older (monitoring shows freshness) |
| DbDataStore's server | entity reads and writes, documents, change requests | model and structure changes, new rules or permissions |
| The console | everything an end user does | administration, approvals made in the console, monitoring views |
| A serving instance | the others behind the load balancer | nothing, if the group has more than one instance |

## 6. High availability, groups and identity

- **Shared state.** Instances in a group share their state on SQL Server or PostgreSQL through `DbDataKit.State`, which already has
  both dialects. That state covers sessions, the identity store, audit, change requests, the published definitions and run lineage.
  SQLite is for a single instance only, and starting a second instance against SQLite fails with a message.
- **A serving group** is what the console pairs with and what Build pairs with as a Publisher (plan 13 W12; 13a T10 targets a group). A
  publish is written once to the group's shared state, and every instance picks it up.
- **Identity is a module of the serving layer by default** (owner, 2026-10-26: end users must be able to sign in without reaching the
  private console).
  - The identity home's public endpoints are served by the serving layer.
  - Managing users, groups and roles stays an administrator's job, done from the console through the management API.
  - Every console, including the administrator's, becomes an OpenID Connect client of the home.
  - The signing keys are shared by the group's instances, from a protected store.
  - **Option:** run the identity home on its own behind the firewall (13a U15), with the serving layer forwarding its public endpoints.
    This keeps the keys and the user store off the edge, for installs that want it.
- **Load balancing** is external (any HTTP or gRPC balancer). Instances are interchangeable, and Flight SQL tickets and sessions are valid
  on any instance of the group.
- **Lineage and monitoring** (plans 13 W10, 18): each instance records its runs and run lineage into the group's state, and the console
  pulls them per group, shown as one node.

## 7. What changes in other plans

| Plan | Was | Now |
| --- | --- | --- |
| 13 D20, W12 | the identity home is hosted by a console by default | hosted by the serving layer by default (section 6); run on its own as the option |
| 13a T10, U13, U15 | Build pairs with a Proxy; the home inside a console | Build pairs with a serving group; the home inside the serving layer; U15 is the separate option |
| 14 R1, R8 | hosting on Proxy; protocol adapters as shared code | unchanged in substance: R1 becomes the general publish model (principle 1), and the adapters are this layer's modules |
| 15 section 11 | decision C: DbDataStore serves itself | **superseded**: DbDataStore's server keeps models and structure; reads, writes, documents and WebDAV are the serving layer's `store` and `webdav` modules; option B's objection (a write path in Proxy) is answered by principle 2, since the write path is DbDataStore's own libraries, only hosted here |
| 15 M9 | DbDataStore serves its entities over Flight SQL | the serving layer's `store` module does |
| 16 section 2 | DbDataApps as a standalone host and a paired client of DbDataStore and Proxy | the `apps` module of the serving layer; it calls the `store` and `semantic` modules in process, under the signed-in user |
| 16 section 2b | the home hosted by a console | hosted by the serving layer (section 6) |
| 17 R2, R4 | feeds and Postgres wire on Proxy | the `feeds` and `pgwire` modules |
| 18 | Proxy's run lineage per table | unchanged, per serving group |
| 19 lanes N, J3, Z | Serving libraries extracted from Proxy; DbDataStore self-hosts | N unchanged (it extracts the modules' code); J3 unchanged; Z builds `DbDataKit.Store.*` as libraries the serving layer hosts, and Z06 (the console grid editor) calls the `store` module |
| `docs/suite-data-flow.md` | Proxy and DbDataStore both serve | redrawn with the serving layer as the single edge |

## 8. Decisions

All confirmed by the owner on 2026-10-26:

1. The `apps` and `mcp` modules are part of the serving layer.
2. Extraction on save runs in the serving layer, where the document arrives. The extraction definitions come from DbDataStore's server.
3. Approving a data change request is a runtime action in the serving layer, the same as the write itself. Approving a *structure*
   change stays in DbDataStore's server.
4. The rename (DbDataServer, DbDataHost or another) is decided later. When it happens, the command and package names change as a clean
   break.

## 9. Slices

Same rules as plan 13a. The letter is **O** (outer layer); slice IDs O01 onwards.

| # | Slice | Needs | Size | Done when |
| --- | --- | --- | --- | --- |
| O01 | Modules in the Proxy host: each endpoint a module enabled per instance in `server.yaml`; health per module; today's Flight SQL service becomes the `flightsql` module | 13a P05 | M | an instance with only `flightsql` serves as today; an unknown module name fails naming the file |
| O02 | Shared state on SQL Server and PostgreSQL through `DbDataKit.State`; a second instance against SQLite refused | O01 | M | two instances share sessions and audit (test on both engines) |
| O03 | Published definitions: a versioned definition store in the group's state, keyed by channel as well as version, each version recording what it depends on (plan 21; `prod` only until channels are built); publish, switch atomically, keep the last good version, roll back | O02 | M | a publish reaches every instance; a failing version leaves the old one serving; works with the publisher stopped (test) |
| O04 | Serving groups: the console pairs with a group; Build's Publisher pairing (13a T10) targets a group | O03, 13a T10 | M | a semantic package deployed once is served by every instance (test with two instances) |
| O05 | High availability in CI: two instances behind a balancer; one stopped during a query run; the back-ends stopped | O04 | M | queries and sign-ins continue; the Flight SQL client reconnects to the other instance (test) |
| O06 | The `identity` module: `DbDataKit.Identity` hosted in the serving layer; public endpoints; signing keys shared by the group; consoles as OpenID Connect clients | O02, 13a U13 | M | Playwright: sign in through the serving layer with the console stopped; the admin console signs in through it too |
| O07 | Identity on its own, forwarded: the home behind the firewall, the serving layer forwarding its public endpoints | O06, 13a U15 | S | the O06 test passes with the home moved out |
| O08 | The `store` module: entity reads (as of a date) over Flight SQL and HTTP; validated writes and change requests through `DbDataKit.Store.*`, from published models | O03, plan 19 Z04, Z05 | M | reads and writes with DbDataStore's server stopped; a rule violation names the rule (test) |
| O09 | The `webdav` module: documents with versions; extraction on save in a child process | O08, plan 15 M5, M6 | M | Excel's recorded traffic against it; a saved workbook updates its extracted entity (test) |
| O10 | DbDataStore's server publishes models, rules and permissions to the group; structure changes stay in it | O03, plan 19 Z03 | S | a new rule is enforced by every instance after publish (test) |
| O11 | The `apps` module: the DbDataApps host as a module; app code in child processes; calls to `store` and `semantic` in process as the signed-in user | O06, O08, plan 16 | M | a code app reads and writes an entity with the back-ends stopped (Playwright) |
| O12 | The `mcp` module: the agents' endpoint, every action approved by the user | O06, plan 16 section 1 | S | an agent's write waits for approval (test) |
| O13 | The outage table (section 5) as tests: one per row | O05, O08 | S | each row's "keeps working" holds with that back-end stopped |
| O14 | The rename, when the owner decides: product, commands, packages; old names refused with a message (clean break) | | S | CLI snapshot; a release note |

**Lanes.** The O lane runs after 13a P05 and beside plan 19's N lane: N extracts the protocol code, and O01–O05 make the host modular
and highly available. Sequence them in Proxy's host files (O01 after N02, or N02 written against O01's module interface). O06–O07 follow
13a U13. O08–O10 follow plan 19's Z lane, which builds `DbDataKit.Store.*` as libraries so they can be hosted here. O11–O12 wait for
plan 16's design.

## 10. The serving contract (design, owner decisions 2026-10-27)

N01's `IServingHost` is the first, small version: one interface that authenticates, describes, queries and optionally writes. The
modules in section 4 need more, so N02 is designed against the contract below. It records seven decisions and what each module takes.

### 10.1 Decisions

1. **Placement: libraries plus a thin host.** Each module's protocol code is a `DbDataKit.Serving.*` library with no reference to Proxy.
   The Proxy host loads modules, configures them from `server.yaml` and supplies the core services. N02 and O01 are designed together.
2. **The core owns the duties; adapters are thin.** An adapter reads credentials and speaks its wire format. The core authenticates,
   holds sessions, applies access rules and limits, audits, caches and traces. A module is never handed a raw executor.
3. **Services per capability, not one request union.** A module takes only the services it needs (10.3).
4. **Sessions are core sessions in shared state** (`DbDataKit.State`), valid on any instance. Protocol-specific state (a Postgres
   prepared statement, an XMLA session) is an opaque blob the adapter owns, kept with a time-to-live.
5. **A query result is an Arrow stream plus provenance:** the schema, counts, the canonical query that ran, the definition version
   and the channel. Adapters convert it (Postgres rows, an XMLA cell set, JSON). MDX cell sets are produced by the semantic service from
   the request's axes, still as Arrow.
6. **Access rules (plan 22) are applied inside the core,** on the plan: the caller's attributes are resolved, the row filter is added
   before execution, and columns are hidden or masked in the result. Adapters only see filtered data. The cache key includes the attribute values.
7. **One rich catalog model.** The core returns the objects a caller may see: datasets with columns, types, keys and descriptions;
   for semantic models, dimensions, hierarchies, and measures with format strings; for entities, as-of support. Each adapter renders its
   own catalog views (Flight metadata, XMLA `Discover`, `pg_catalog`) from it, using `DbDataKit.Metadata` types where they fit.

### 10.2 The module contract

A module is a listener (or a set of routes) with: a name, the options it reads from `server.yaml` (an unknown name fails naming the
file), start and stop, a health report, drain (stop taking new work, finish running work; plan 24), and a subscription to the
published definitions it serves. Modules are enabled per instance.

### 10.3 Core services

| Service | What a module can ask | Used by |
| --- | --- | --- |
| Identity | authenticate credentials (password, bearer, Windows where the transport allows, SCRAM); resolve the caller's roles and attributes | every module |
| Sessions | create, resume and close a session; hold the adapter's opaque state | `flightsql`, `pgwire`, `xmla`, `mcp` |
| Query | run a typed request (SQL pass-through, semantic request, semantic SQL, MDX) under the caller's limits, rules, channel and draft; cancel | `flightsql`, `semantic`, `pgwire`, `xmla`, `feeds`, `apps` |
| Catalog | describe what the caller may see (7 above) | the protocol modules |
| Entities | as-of reads, validated writes, change requests, drafts | `store`, `apps`, `mcp` |
| Documents | versioned documents, extraction on save | `webdav`, `apps` |
| Definitions | the current published version per channel; a request is pinned to one version while it runs | all |
| Platform | audit, limits, cancellation, the result cache, OpenTelemetry, child processes for heavy work | all |

`identity`, `apps` and `mcp` use Identity and Platform and call Query, Entities and Documents in process, as the signed-in user.

### 10.4 The caller context

Every call carries one context: the identity, roles and attributes; the channel and any preview draft (plan 21); the session; a
cancellation token; and a trace ID. Services take it as their first argument, so no module can leave it out.

### 10.5 What N01 becomes

`IServingHost` is kept as the small host a fake or a test can implement. N02's first step maps it onto the services above (its
`AuthenticateAsync`, `DescribeAsync`, `LimitsFor`, `QueryAsync` and `Writes` become Identity, Catalog, Platform, Query and Entities).
J10 stays behind its adapter until that mapping lands.

### 10.6 Sequencing

N02 is the first module on this contract: a design review of 10.1 to 10.4 against all ten modules, then the contract in
`DbDataKit.Serving.Abstractions`, then Flight SQL moved onto it with no behaviour change. Proxy's Flight SQL suite is the safety net.
