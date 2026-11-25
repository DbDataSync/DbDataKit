# Reading a database while a project is authored

A person who authors a build, sync or store project in the Workspace needs to know what the project's databases hold: which databases, schemas,
tables and columns a connection reaches, what a query returns, whether a connection opens, and whether the login a run will use may do what the
project asks of it. This page says who reads the database for that, with which login, what is kept and where, and what is checked without a
connection at all. It applies to every kind of project; what a sync project checks is [its own section](#a-sync-project).

**Built today:** the catalog cache (`catalog-cache.yaml`, `catalog-cache/2`, below) filled from a Runner channel (**Refresh from a Runner** on the tab
**Catalog**, with the channel picker and the difference shown first, [below](#the-catalog-cache)) or from a Gateway's data source through the console
(**Refresh from a Gateway** on the same tab); the offline readers of the
cache (pickers, a Sync mapping's columns, the target side of Sync's provisioning plan, Build's import of models from the cache); the offline checks of a Sync draft against the cache ([The offline findings](#a-sync-project)); the Runner's slot binding
test, of a published version or of a draft by its package, channel and slot ([below](#testing-a-connection)); a staged version's manual tasks (Sync's `provision`, Build's `status` and `check`, the Store's `deploy` dry run and `contract`
plan); the Runner's built-in package `dbdatakit-metadata` with its tasks `discover` and `describe`, and the Runner route a console starts them with
([below](#the-built-in-package-dbdatakit-metadata)); the console's routes that read a connection's shape for a Workspace project from a Runner channel
or a Gateway's data source and describe a query ([below](#how-a-screen-asks-for-a-read)); the shared pieces of the screens ([below](#every-kind-uses-the-same-pieces)):
the channel picker with **Set up for development**, **Refresh**, **Test**, **Describe columns** and the newest-check card; a Sync mapping's **Preview rows** and a
segmenting strategy's **Try**, answered to the screen and kept nowhere ([below](#rows-and-trials-are-answered-never-kept)). **Not built yet:** everything else on this page (the subjects [AUTHORING](../planning/AUTHORING.md) and
[SYNCAUTHORING](../planning/SYNCAUTHORING.md) build it).

## Who reads: the Runner, with the login the channel binds

Every read of a customer database made for authoring runs on the Runner, as a task in a process of its own, with the login a channel binds to the
project's slot: the slot's `read` role where one is bound, else its `default`. The run is recorded as the person who asked. The Workspace and the console
never open the connection and hold no login for it.

Why: the console and the Workspace are Control, which holds no customer-data login ([architecture.md](architecture.md#what-runs-where)); the Workspace
also runs authors' build children and script compiles, the last place a login should sit. Reading with the bound login has a second reason: the answer
says what the run will be able to read, not what someone else's login sees. Ana's own login sees `hr.salaries` and the run's does not; a cache made with
Ana's login would offer a table no run can read.

A person never reads with a login of their own. A login a person types is used in one way only: as the login of a slot in a non-production channel, sent
to the Runner once (below). Integrated sign-in (Windows, Entra) cannot be typed or delegated from a browser to a server in any case.

Who may ask: anyone with access to the project, in every channel, `prod` included. A read in `prod` is shown with a clear warning that it reads
production (the database and the channel named, shape only, no row) and then goes ahead; no approver role is needed. Some syncs are designed between
systems that have no development copy, and they cannot be designed without reading production's shape. How the suite treats such actions:
[architecture.md](architecture.md#risky-actions). A setting an administrator may turn on to restrict
production reads is a later hardening option.

## The built-in package `dbdatakit-metadata`

The Runner ships a package of its own, `dbdatakit-metadata`, that answers shape only, from `DbDataKit.Metadata`'s readers. It is started for a project's
package, a channel and a slot, resolves the slot through that channel's binding as the binding test does, and refuses a slot the project's package does
not declare. Its tasks:

| Task | Answers | Never |
| --- | --- | --- |
| `discover` | databases, schemas (empty ones too), tables and views, columns with their type text, nullability, primary and unique keys, foreign keys; within the schemas or the name pattern given | a row, a value, a statistic of values |
| `describe` | the columns of a query: the read-only policy (`QueryProbe.RequireReadOnly`) first, then the engine's own describe (`sp_describe_first_result_set` on SQL Server, PostgreSQL's schema-only describe, a wrapped zero-row probe elsewhere) | a row: the query is described, not run |
| `facts` | the server's version and the settings of the database a kind's checks name | a value of a table |

`discover` and `describe` are built; `facts` is not built yet.

**How a read is started.** A console calls the Runner for a person it signed in:

```
POST /api/v1/metadata/dev/discover
{"package": "crm-to-dw", "slot": "crm", "inputs": {"schemas": "[\"dbo\"]"}, "on_behalf_of": "Ana"}

202 {"run_id": "01a2...", "task": "dbdatakit-metadata/dev/discover", "package": "crm-to-dw", "slot": "crm", "channel": "dev", "step": "discover", "key": "01a2..."}
```

- The call needs the operator role and a console's token for a person. A call without `on_behalf_of` is refused with 400, and a call from a client
  that is not a console is refused with 403.
- The package must declare the slot in the version the channel serves or in the one staged there; otherwise the answer is 404. The slot must be bound
  in the channel; otherwise the answer is 409, which says to bind it on the Runner's Packages page (Bindings).
- The task reads with the connection's read login when the connection has one, else with its default login. The rule that keeps a non-production
  channel off production's connection applies to these reads as it does to the package's own runs.
- The run is recorded as any run is: the audit line `runner.task.run` names the person (`on_behalf_of`) beside the console. A refused call is audited as
  `runner.metadata.read`, without the query.
- The generic run route (`POST /api/v1/tasks/dbdatakit-metadata/...`) refuses the package with 409, because only the metadata route checks the slot.

Inputs: `discover` takes `schemas` (a JSON list; none means every schema), `pattern` (an SQL `LIKE` pattern over table names) and `table` (one table
of the one schema given). `describe` takes `sql`, the query. `discover` reads the database the connection opens; another database of the same server is
read through a connection of its own.

**Where the answer is.** Each run keeps its answer in a step named after the task, keyed by the run's id:

- `discover`: a JSON file, one connection of `catalog-cache/2` without its `read` (the caller adds the server and the channel it asked). Its id is the
  `payload` of `GET /api/v1/tasks/dbdatakit-metadata/dev/discover/steps/discover?key=<run id>`, and
  `GET .../steps/discover/payloads/<payload>` returns the file (`RunnerPublishClient.StepsAsync`, then `StepPayloadAsync`).
- `describe`: the step's value, `{"engine": "sqlserver", "strategy": "SqlServerDescribeFirstResultSet", "columns": [{"name": "id", "type": "int",
  "nullable": false}]}`.

A query that writes (`UPDATE crm.orders SET ...`) fails the `describe` run with the read-only policy's reason, before the connection opens.

**Two reads at once.** Runs of the package overlap: when two authors read in `dev` at the same moment, both runs start at once.

**Where it comes from.** Each Runner carries the package in `builtin/dbdatakit-metadata/` beside its program and puts it in a channel the first time a
read is asked there, and again when its own files differ from what the channel serves. In a group whose members run different versions, the channel
serves the version of the member that was asked last. The package runs in the task host, in a process of its own per read. It opens SQL Server,
PostgreSQL, MySQL, MariaDB and Oracle.

A connection whose driver the built-in package cannot run is read by the project's own package instead, which needs a build staged in that channel.

### How a screen asks for a read

A Workspace screen asks the console, and the console asks the Runner or the Gateway. The Workspace server only tells the console which package the
project becomes and whether the person may read it. It never calls the Runner or the Gateway and never sees the login.

The console has these routes, each under `api/workspace-catalog/{workspace}/{project}` (`{workspace}` is the Workspace server's id in the console):

| Route | What the console does |
| --- | --- |
| `GET .../channels?runner=&connection=` | lists the channels of the project's package on that Runner, whether each binds the connection's slot, and the first channel that is not `prod` and binds it (`default`): what the channel picker offers |
| `POST .../read` with `{"runner", "channel", "connection", "schema"?, "table"?}` | starts `discover` for the person and waits up to 10 seconds for it |
| `GET .../read/{run}?runner=&channel=&connection=` | answers how the run started earlier stands |
| `POST .../describe` with `{"runner", "channel", "connection", "sql"}` | starts `describe` of the query for the person and waits up to 10 seconds for it ([below](#how-a-screen-asks-for-a-querys-columns)) |
| `GET .../describe/{run}?runner=&channel=&connection=` | answers how the description started earlier stands |
| `POST .../gateway-read` with `{"gateway", "connection", "data_source"?, "adopt"?}` | asks a Gateway for the tables and columns of one of its data sources, as the person ([below](#a-read-from-a-gateway)) |
| `POST .../binding-test` with `{"runner", "channel", "connection", "role"?}` | asks the Runner's test of the connection's slot in the channel, as the person ([Testing a connection](#how-a-screen-asks-for-a-test)) |

For each call, the console first asks the Workspace server for the project (`GET /api/v1/projects/{project}`) as the signed-in person. The answer says
whether the person may read the project and which package the project becomes on a Runner. The Workspace server's refusal is passed on with its own
words. An app or a Store project is not read from a Runner: its kind makes no single package of tasks.

The slot is the one the package makes of the connection's name: lower-case, with every character other than a letter, a digit or `_` turned into `_`
(`CRM-db` is read through the slot `crm_db`). A Build project's connection names already are slot names.

The console then starts the read on the Runner as the person (`on_behalf_of`). The token it sends carries the person's roles on that Runner, so a
person who holds only the viewer role there is refused (403, the operator role is needed). A read in `prod` goes ahead: the screen warns before it
asks.

What `POST .../read` answers:

- The read finished within 10 seconds: 200, `"state": "done"`, and `cache`, the text of a `catalog-cache/2` file with that one connection, recorded as
  read from the Runner (its name in the console) and the channel. The console checks it with the cache's strict reader first.
- The read is still going: 202, `"state": "running"`, the run's id and `link`, the run's page in the console. The screen asks
  `GET .../read/{run}` until the state changes.
- The read failed: 422, `"state": "failed"`, and the Runner's reason in `error`.

Example: Ana reads the table `dbo.accounts` of the connection `crm` in `dev`.

```
POST /api/workspace-catalog/ws-1/crm-to-dw/read
{"runner": "runners-eu", "channel": "dev", "connection": "crm", "schema": "dbo", "table": "accounts"}

200 {"state": "done", "run_id": "01a2...", "link": "/servers/runners-eu/runs?run=01a2...", "channel": "dev", "production": false,
     "connection": "crm", "tables": 1, "columns": 4,
     "cache": "schema: catalog-cache/2\ngenerated_at: '2026-11-22T10:00:04Z'\nconnections:\n  'crm':\n    engine: 'sqlserver'\n    read: {server: 'runners-eu', product: 'runner', channel: 'dev', at: '2026-11-22T10:00:04Z'}\n..."}
```

The screen compares the new entry with the cache it holds and shows the difference before it replaces the entry
([Refresh on request](#the-catalog-cache)).

#### How a screen asks for a query's columns

`POST .../describe` starts the built-in task `describe` on the Runner, for the person, in the channel given, with the login that channel binds to the
connection's slot. The query is described and never run. The console waits up to 10 seconds, as for a read:

- The description finished: 200, `"state": "done"`, the engine, the strategy the engine used, and `columns`, each with its `name`, its `type` as the
  engine names it and `nullable`. Nothing else of the Runner's answer is passed on.
- It is still going: 202, `"state": "running"`, the run's id and `link`. The screen asks `GET .../describe/{run}` until the state changes.
- It failed: 422, `"state": "failed"`, and the Runner's reason in `error`. A query that writes fails here, with the read-only policy's reason.

A query is at most 100,000 characters. The screen decides where the columns go: the kind's own file ([below](#the-catalog-cache)).

Example: Ana describes a query over `crm` in `dev`.

```
POST /api/workspace-catalog/ws-1/crm-to-dw/describe
{"runner": "runners-eu", "channel": "dev", "connection": "crm", "sql": "SELECT id, segment FROM dbo.accounts"}

200 {"state": "done", "run_id": "01a3...", "link": "/servers/runners-eu/runs?run=01a3...", "channel": "dev", "production": false, "connection": "crm",
     "engine": "sqlserver", "strategy": "SqlServerDescribeFirstResultSet",
     "columns": [{"name": "id", "type": "int", "nullable": false}, {"name": "segment", "type": "nvarchar(20)", "nullable": true}]}
```

#### A read from a Gateway

A connection that is a data source of a Gateway the console manages can be read through the Gateway's schema call
(`POST /api/v1/data-sources/{name}/schema`). The Gateway reads with its own login and answers names, types and keys, never a row. The console asks it
as the signed-in person, after the Workspace server said the person may read the project. An app is refused: it has no connections.

- `gateway` is the Gateway's id or name in the console. A server that is not a Gateway is refused with 409.
- `connection` is the project's connection the cache entry is for. `data_source` is the Gateway's data source; without it, the connection's name is
  the data source's name.
- The Gateway decides what the person may read. An administrator of the Gateway reads any data source. A publisher reads the data sources the
  console's deploys made.
- A data source made by hand is read only once the person confirmed it. The first call answers 409 with `adopt` (the data source and who made it);
  the screen shows the confirmation and asks again with `"adopt": true`. The Gateway records who adopted it and when.

The answer is 200 with `cache`, the text of a `catalog-cache/2` file holding that one connection, recorded as read from the Gateway (its name in the
console, with no channel). The console checks it with the cache's strict reader first.

Example: Ana reads the data source `dwh` of the Gateway `edge` for the connection `warehouse` of the Build project `sales-dw`.

```
POST /api/workspace-catalog/ws-1/sales-dw/gateway-read
{"gateway": "edge", "connection": "warehouse", "data_source": "dwh"}

200 {"state": "done", "gateway": "gw-1", "gateway_name": "edge", "connection": "warehouse", "data_source": "dwh", "tables": 12, "columns": 96,
     "cache": "schema: catalog-cache/2\ngenerated_at: '2026-11-23T10:00:00Z'\nconnections:\n  'warehouse':\n    engine: 'postgres'\n    read: {server: 'edge', product: 'gateway', at: '2026-11-23T10:00:00Z'}\n..."}
```

The screen shows the difference and replaces the entry on **Accept**, as for a Runner's read.

## Before anything is published: Set up for development

A read needs a binding in some channel, not a published version. A person who works on a draft can give the project's Runner a development login for
a connection, so the connection can be read in the channel the person picks before anything is published.

### How a screen sends a development login

A Workspace screen sends the login to the console, never to the Workspace server:
`POST /api/workspace-dev-logins/<workspace server>/<project>` with the Runner, the channel, the project's connection, and the login the person typed
(`user` is optional, `password` is required). The shell sends this route to the console even when the screen talks to its Workspace server through the
relay, as it does for the reads above.

What the console does with it:

1. It checks the request: the channel, the connection, the password, and the user when one is sent. A user has 1 to 256 characters, no space at
   either end and no control character.
2. It asks the Workspace server for the project (`GET /api/v1/projects/{project}`) as the signed-in person, as for a read. That request carries no part of
   the login. The answer says whether the person may read the project and which package it becomes on a Runner.
3. It stores the password in the Runner's secret store (`PUT /api/v1/secrets/{name}` on the Runner) as the signed-in person. The Runner asks for its
   admin role, as it does for every secret it stores, and records the person in its audit with the name and length of the value.

The password is the only secret stored. The user is not a secret: the console gives it back in its answer as plain text, so the screen can show it,
and stores it nowhere. The user belongs in the text of the connection's copy on the Runner, beside the reference to the password's secret.

The Runner holds the password under a name of the kind `dev-login`, made of the package, the channel and the slot:
`dbdatakit:runner:dev-login:<package>.<channel>.<slot>:password`. A person reading the Runner's secrets overview can tell from the name that the
secret came from a Workspace screen, and for which package, channel and slot.

The console accepts any channel, `prod` included. A person who holds a production login and the Runner's admin role can store it for `prod` the same
way. The password of each channel is stored under its own name, so a login stored for `dev` is never used by `prod`.

The login never reaches the Workspace server, so it is in none of its state, audit or log. A refusal of the Workspace server or of the Runner is passed
on with its own words.

Example: Ana sets up the connection `crm` of the project `crm-to-dw` for `dev` on the Runner `runners-eu`.

```
POST /api/workspace-dev-logins/ws-1/crm-to-dw
{"runner": "runners-eu", "channel": "dev", "connection": "crm", "user": "ana_dev", "password": "..."}

200 {"package": "crm-to-dw", "connection": "crm", "slot": "crm", "channel": "dev", "production": false,
     "runner": "runners-eu", "runner_name": "runners-eu", "user": "ana_dev",
     "secrets": {"password": "dbdatakit:runner:dev-login:crm-to-dw.dev.crm:password"}}
```

The answer names the secret the Runner now holds, never its value. `production` is `true` when the channel is `prod`.

**Set up for development in the channel picker.** The channel picker offers **Set up `crm` for development** when a channel other than `prod` does not
bind the connection's slot, or when no channel binds it (also when the Runner holds no version of the package yet and lists no channel). The person picks
the channel (the unbound ones other than `prod`; `dev` when the Runner lists none), types the user and the password once, and presses **Send the login
to `<Runner>`**. The screen sends them with the route above and keeps nothing of the password. The answer is shown as "runners-eu holds the login of crm
for dev." While the channel does not bind the slot yet, the line also says what an administrator binds, and **Ask runners-eu again** reads the channels
again once it is bound.

**Planned.** The console also puts the connection's copy on the Runner with the user as plain text and the password naming the stored secret, and
binds the slot in the channel the person picked, so no administrator has to. The console's posture page lists the bindings made this way.

**Until then, an administrator binds the slot by hand.** The project's package must be on the Runner, and the Runner's own bindings screen (its Packages
page, Bindings) binds the slots of a version it holds:

1. Publish a build of the draft to the Runner's `dev` channel, staged rather than served (the Workspace's Builds tab, whose publish can also bind each
   slot to a connection).
2. Store the development login on the Runner: with the route above, or on the Runner's Packages page, which sets a run login.
3. An administrator binds the connection's slot in `dev` on the Runner's bindings screen. The console sends the connection's copy with the user as
   plain text and the password naming the secret the Runner holds.

Refresh then reads in `dev`. When no channel binds the slot, the channel picker names the channel and this way to bind one.

## The catalog cache

What discovery finds is written into the draft's `catalog-cache.yaml`, an unsaved edit the person commits with the draft, so a build, a review and an agent
read the same thing. The cache records, per connection, where and when it was read: the Runner group and the channel (`{server, product, channel}`), so a
mapping made against `dev`'s shape is checked against `prod`'s at staging (below).

The format is `catalog-cache/2`. Per connection it holds the engine, where and when it was read (`read`: the server's name and product, never an
address; the channel for a Runner, none for a Gateway, which reads with its own login), the databases it reaches with every schema of each, the empty ones
too (so a picker can offer a schema that has no table yet), and its tables and views: columns with their type text, nullability and primary key, unique
keys, and foreign keys with the table and columns they reference. A connection whose databases were not read (a Gateway's answer) has `databases: []` and
tables without a database.

The tables are one flat list, each naming its database and schema, beside the `databases` list that holds every schema, the empty ones included; tables
are not nested under their databases and schemas. Every reader (Build's import, Sync's provisioning plan, the pickers) works on a list of tables, and a
diff of the file stays one line per table; what matters is that each table knows its database, so `crm.dbo.orders` and `crm_archive.dbo.orders` are two
entries that never merge. Unique keys are kept, since `discover` answers them and the cache records what it answers.

```yaml
schema: catalog-cache/2
generated_at: 2026-11-18T10:00:04Z
connections:
  crm:
    engine: sqlserver
    read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:04Z}
    databases:
      - {name: crm, schemas: [dbo, staging]}      # staging has no table yet
    tables:
      - database: crm
        schema: dbo
        name: orders
        kind: table
        columns:
          - {name: id, type: int, nullable: false, key: true}
          - {name: customer_id, type: int, nullable: false}
        foreign_keys:
          - {name: fk_orders_customers, columns: [customer_id], references: {schema: dbo, table: customers, columns: [id]}}
```

The strict reader (`DbDataKit.Catalog.Cache`, and its twin `catalogCache.ts` in the browser, run over the same fixtures) accepts these keys and no other:
a key that would carry a row, a value or a statistic of values (`rows`, `sample`, `min`, `distinct`...) or a login is refused by name, and a value that
looks like a password, a connection string, an address with a login or a secret reference is refused wherever it stands. A table names a listed database
and one of its schemas when the connection lists databases. A file of `catalog-cache/1` is refused naming the file: it is deleted and each connection
refreshed, which writes it again as format 2. It is never converted, and a refresh of one connection is refused too while the file is format 1, so
nothing is dropped silently: Ana opens a draft with a format-1 cache of `crm` and `dw` and presses Refresh on `crm`; the refresh says the file is
format 1 and to delete it, and she deletes it and refreshes both. The draft's check, the Catalog tab, `provisioning plan --from-cache` and
`import --from-cache` refuse it the same way.

**Refresh on request, with the difference shown.** Nothing refreshes by itself. **Refresh** on a connection or on one table reads again in the channel the
cache says it came from; the Designer shows what changed (columns added, removed, retyped) before the cache is replaced, and the draft's check then names
every mapping or model the change touches. The screen makes that comparison itself, against the cache as it holds it, unsaved
edits included; the read answers only the new entry, and nothing of the draft is sent with the request.

On the Files area, the tab **Catalog** has **Refresh from a Runner**. The person picks a connection of the draft, and either the whole connection or one
of its cached tables. The channel picker lists the Runners paired with the console and the channels of the project's package on the chosen Runner:

- The cache says the connection was read from a Runner channel, and that channel still binds the slot: that Runner and channel are offered first.
- Otherwise: the first channel that is not `prod` and binds the slot is offered.
- A channel that does not bind the slot is named, with how to bind it ([Before anything is published](#before-anything-is-published-set-up-for-development)).
- The person picks `prod`: a warning says the read is of production, with production's login, names and types only. **Refresh** still reads.

**Refresh** asks the console ([How a screen asks for a read](#how-a-screen-asks-for-a-read)). When the read takes longer than 10 seconds, the tab
shows a link to the run and keeps following it. When the read ends, the tab lists every table and column added, removed or retyped, then:

- **Accept** puts the new cache in the draft as an unsaved edit, read the same way by the Designer and the other tabs. **Save** keeps it in the draft.
- **Dismiss** leaves the cache as it was.

A read of one table replaces that table and keeps the connection's other tables. A read of the whole connection replaces the connection. The other
connections of the cache stay as they are. A cache the strict reader refuses (a file of format 1, or a damaged one) is not replaced: delete or fix the
file, then refresh again.

Example: on the tab **Catalog** of the draft of `crm-to-dw`, Ana picks `crm` and `dbo.accounts`; the picker offers `dev` on `runners-eu`, where the cache
was read last week. She presses **Refresh**; the tab says "1 column added" and lists `crm.dbo.accounts.segment`. She presses **Accept**: the cache is an
unsaved edit recorded as read from `dev` today, and the chrome counts one unsaved file until she saves.

The same pieces (`ChannelPicker` and `CatalogRefresh` of `@dbdatakit/workspace`) are what a kind's Designer offers on its screens. Under **Refresh**,
the tab also offers **Test `<connection>` in `<channel>`** when the picked channel binds the slot ([Testing a connection](#testing-a-connection)).

**A query's columns** (**Describe columns**, a Build model's query or a Sync query source) go where the kind keeps them: a Sync mapping's column cache, a
Build model's descriptor. The shared piece shows the columns with their types and hands them to the kind's screen on **Use these columns**; the kind
writes them into its file as an unsaved edit.

**A Gateway data source is a second way to fill the cache.** A connection that is already a data source of a Gateway the console manages can be read
through the Gateway's schema call, with the Gateway's own read login ([A read from a Gateway](#a-read-from-a-gateway)). It is the natural source of a
Build model over a served warehouse. No data source is made on a Gateway only so that authoring can read it (a Sync target, or an operational source the
Gateway does not serve).

On the tab **Catalog**, **Refresh from a Gateway** has a Gateway picker (the Gateways the console manages), the draft's connection, and the data source
(blank: the connection's name). **Refresh catalog from `<data source>`** asks the console. The tab lists what changed, then **Accept** puts the new cache
in the draft as an unsaved edit and **Dismiss** leaves the cache as it was. A data source made by hand shows a confirmation first; **Confirm and read**
reads it, and the Gateway records the adoption.

Example: on the tab **Catalog** of the draft of `sales-dw`, Ana picks the Gateway `edge`, the connection `warehouse` and the data source `dwh`, and
presses **Refresh catalog from dwh**. The tab says "Read 12 tables and 96 columns of warehouse from the data source dwh on edge" and "2 tables added". She
presses **Accept**, then saves the draft.

### Offline and live

From the committed cache, with no connection, as the person types: the pickers (databases, schemas, tables, columns), a mapping's or a model's columns and
types, the draft's check (a named table or column missing from the cache, a type the engine's type map does not know, the key columns), the provisioning
plan's target side, Build's import of models from the cache.

Live, as a Runner run in a channel: discovery, a query's columns, the connection test, and everything that depends on the login or on the server's settings
(privileges, change tracking and capture, `wal_level`, slots, the state's schema), through each package's `preflight`.

### What is kept where, and what never is

| What | Where |
| --- | --- |
| the shape of a connection | `catalog-cache.yaml`, committed in the draft |
| a query's columns | the kind's own file |
| check findings, test steps | the Runner's run record (step outputs, under its retention); the newest-check card shows the newest run per channel with its time and link ([below](#every-kind-uses-the-same-pieces)). Not committed: they describe a login at a moment, not the project |
| the rows of a Sync mapping's **Preview rows** and the candidates of a strategy's **Try** | on the screen that asked, after the console took them from the Runner's memory ([below](#rows-and-trials-are-answered-never-kept)); never in the draft, a run record, a log or an audit |
| a row or a value of a table, a sample, a statistic of values (minimum, maximum, distinct values, segment bounds), a login, a connection string, an address | nowhere; the cache's strict reader refuses them by key and by look |

## Testing a connection

**Test** on a connection runs the Runner's slot binding test in a channel: the binding, the connection document, the login's secrets, then a connect
(reach, TLS, login, the server's version), each step said. No row and no sample is shown: a configured test query is run only as the probe, its rows
neither shown nor kept.

A test runs through the Runner in every case. It needs a binding in the channel, not a version: before anything is published it runs in a development
channel once the slot is bound there (Set up for development, above), and after publish in any channel that binds the slot. The binding test reads the
binding, never a staged or served version.

### How a screen asks for a test

A Workspace screen asks the console: `POST /api/workspace-catalog/<workspace server>/<project>/binding-test` with the Runner, the channel, the project's
connection and optionally `role` (`default`, `read` or `write`; `default` when none is given). The draft needs no build.

What the console does:

1. It asks the Workspace server for the project (`GET /api/v1/projects/{project}`) as the signed-in person, as for a read. The answer says whether the
   person may read the project and which package it becomes on a Runner.
2. It asks the Runner's binding test for that package, the channel and the connection's slot
   (`POST /api/v1/bindings/{channel}/integration/{package}/test`) as the signed-in person. The Runner asks for its admin role and records the person in
   its audit.
3. It records the test in its own audit as `build.binding_test`, under the person, with the project, the package, the channel and the slot.

The Workspace server is asked only about the project, and it never calls the Runner. A refusal of the Workspace server or of the Runner is passed on in
its own words: a person who may not read the project gets the Workspace server's refusal, and a person who is not an administrator of the Runner gets
"This needs the admin role."

The answer is 200 when the Runner tried the binding, whether the binding passed or failed. `ok` says whether it passed, `step` is the step the Runner
stopped at, and `message` says why in words. `steps` lists the four steps in order, each `passed`, `failed` or `skipped` (not tried):

- `binding`: the slot is bound in the channel.
- `connection`: the Runner has the connection the binding names, and its document loads.
- `login`: every secret the login names resolves on the Runner.
- `connect`: the connection opens.

Example: Ana tests the connection `crm` of the project `crm-to-dw` in `dev`, where an administrator bound the slot `crm` to the Runner's
connection `crm_dev`. Nothing of the project is published yet.

```
POST /api/workspace-catalog/ws-1/crm-to-dw/binding-test
{"runner": "runners-eu", "channel": "dev", "connection": "crm"}

200 {"package": "crm-to-dw", "connection": "crm", "slot": "crm", "channel": "dev", "production": false, "role": "default",
     "runner": "runners-eu", "runner_name": "runners-eu", "ok": true, "step": "connect", "connected": true, "bound_to": "crm_dev",
     "message": "The slot 'crm' opened a connection through 'crm_dev'.",
     "steps": [{"step": "binding", "state": "passed"}, {"step": "connection", "state": "passed"},
               {"step": "login", "state": "passed"}, {"step": "connect", "state": "passed"}]}
```

In `prod`, where nothing binds the slot yet, the same call answers `"ok": false`, `"step": "binding"` and "The slot 'crm' is not bound in channel
'prod'.", with the other three steps `skipped`. The answer never holds a row, an address or a login.

A test of a version the console deployed goes through the deploy instead (`POST /api/deploys/{id}/binding-test`,
[Runs of a deployed build](publishing.md#runs-of-a-deployed-build)). Both ask the same test of the Runner.

## Rows and trials are answered, never kept

Two reads of a Sync project show values of a table: **Preview rows** of a mapping and **Try** of a segmenting strategy. Each is a manual task of the
replication set's package, run on the Runner. The console starts it as the signed-in person, and the result is the run's answer: the Runner holds it in
memory, the console takes it once and passes it to the screen, and nothing keeps it.

| Task | What it reads | What it answers |
| --- | --- | --- |
| `preview` | the mapped source columns of the mapping's source table, with the mapping's filter (every column of a query source), with the run login the channel binds to the source's slot; 20 rows, or up to 100 when asked | the columns, the rows, and whether more rows follow |
| `try-strategy` | a segmenting strategy run over a mapping and a column as a reload runs it: its SQL on the source or the target with the run login the channel binds, a DuckDB strategy in memory, a script with both connections | every candidate, selected or not: its label, the column, the bounds and whether it is selected |

The strategy is either one the replication has (the input `name`) or the one the person is editing, sent as JSON (the input `strategy`), so it can be
tried before it is saved. Both tasks read the mapping as the deployed version has it: an edit that is not saved, built and published is not in the
rows. A strategy's SQL is the author's own and runs with the login a reload uses, so it can do what that login may do.

### How a screen asks for rows

The screen takes the newest deploy of the draft to a Runner and asks the console to run the task and wait for its answer:

```
POST /api/deploys/01a2.../runs
{"task": "preview", "answer": true, "inputs": {"replication": "orders", "mapping": "dbo.orders", "rows": "20"}}

200 {"state": "done", "run_id": "01a3...", "runner": "r-1", "runner_name": "runners-eu", "channel": "dev", "production": false,
     "link": "/servers/r-1/runs?run=01a3...",
     "answer": {"replication": "orders", "mapping": "dbo.orders", "connection": "crm", "source": "crm.dbo.orders",
                "columns": ["id", "name"], "rows": [[1, "Ana"], [2, "Ben"]], "more": true, "limit": 20}}
```

What the console answers:

- The run answered within 10 seconds: 200, `"state": "done"`, and `answer`.
- The run is still going: 202, `"state": "running"`, the run's id and `link`. The screen asks `GET /api/deploys/{id}/runs/{run}/answer` until the
  state changes.
- The run failed: 422, `"state": "failed"`, and the reason in `error`. When the database refused the read or the strategy's SQL, the reason has the
  database's own words, which can quote a value of the table. Those words are in the answer only; the run's record says that the read failed and that
  the reason went to the person who asked.
- The answer was taken already, or it waited more than 10 minutes: 410, `"state": "gone"`. Ask again.

The person needs what every run call needs ([Runs of a deployed build](publishing.md#runs-of-a-deployed-build)): to read the project on the Workspace
server, and the operator role on the Runner. The console's audit names the run, never a value.

A preview and a trial run in every channel the draft was deployed to, `prod` included, for the same person and role. In `prod` the rows are
production's, read with the login production binds, so the screen warns above the answer: "This reads rows of production (prod on plant) with the
login production binds. The rows are shown here and kept nowhere." A strategy's **Try** in `prod` runs its SQL on production the same way.

### What the Runner does with an answer

The task hands its answer to the Runner over its socket (the protocol's `Answer`). The Runner keeps it in the memory of the process that ran the task:

- It gives it once, to the console that started the run, for the same person: `GET /api/v1/runs/{id}/answer?on_behalf_of=<person>`. Ben cannot take
  the rows of a preview Ana started.
- It forgets an answer nobody took after 10 minutes. One answer is at most 2 MiB of JSON, and a Runner holds at most 64 MiB of answers, forgetting the
  oldest first.
- It never writes an answer to its state, a step, the run's log or the audit. The run record keeps the inputs (the mapping, the strategy's SQL) and
  the outcome.

In a group of Runners, the answer is held by the instance that ran the task. A call that reaches another instance answers `gone`.

Example: Ana edits the strategy `by-month` of the replication `orders` on the Bulk Load sub-tab. She picks the mapping `dbo.orders`, types the column
`placed_at` and presses **Try**. The Runner `runners-eu` runs her SQL on `crm` with the login `dev` binds, and the screen shows "12 candidates of by-month
over placed_at, 3 selected." with each candidate's bounds. She changes the query, presses **Try** again, and then **Update** and **Save settings**.
Nothing of the candidates is in the draft or on the Runner.

## Checking before production: `preflight`

A package's read-only manual task `preflight` reads its databases with the logins its channel binds and reports findings. Build's runs `connection status`
for each connection, and the Store's runs the `deploy` dry run and the `contract` plan. A Sync set's preflight reads the source and target of each
mapping ([A sync project](#a-sync-project)).

When a person serves a version in `prod` from the publish panel, the console stages it held, runs `preflight` there with production's bound logins,
and shows the findings with the run's link. With no error the console activates the version. An error leaves the version staged until the person
presses **Activate anyway**, and the Runner records that confirmation with the activation. After publish, **Re-check** on the Runs tab runs the same
task on the version the channel serves. [Preflight before activation](publishing.md#preflight-before-activation) has the calls and an example.

## Every kind uses the same pieces

The built-in package, the cache, the channel picker, **Refresh**, **Describe**, **Test** and the newest-check card are one implementation
(`DbDataKit.Authoring`, `Shared/web/workspace`) that each kind's Designer and Files area use. The screens' pieces are exported by `@dbdatakit/workspace`:

| Piece | What it does | Where it shows today |
| --- | --- | --- |
| `useChannelChoice`, `ChannelPicker` | the Runner and the channel to read in; offers **Set up for development** where a channel does not bind the slot | every piece below |
| `CatalogRefresh` | **Refresh** of a connection or one table, the difference shown before **Accept** | the tab **Catalog**; the Sync Designer's Refresh buttons |
| `BindingTest` | **Test**: the Runner's binding test in the picked channel, each step with its state | the tab **Catalog**; **Test** on each endpoint card of the Sync Designer |
| `QueryDescribe` | **Describe columns** of a query, handed to the kind on **Use these columns** | the Sync Designer's card **Query columns** of a mapping whose source is a query |
| `NewestChecks`, `NewestCheck` | per Runner and channel the draft was published to, the newest `preflight` run: version, outcome, time, findings, the run's link | the Runs tab, under **Re-check** |
| `usePublishedChannels`, `useNewestRun` | the channels the draft was published to, and the newest run of a task in one of them | the newest-check card; the Sync Designer's **Newest preflight** on the Provisioning sub-tabs |
| `useRunAnswer`, `RunAnswerStatus` | a manual task of the version the draft's newest deploy put on a Runner, run for the person through the console, and its answer ([above](#rows-and-trials-are-answered-never-kept)); a run longer than 10 seconds is followed by its link | the Sync Designer's **Preview rows** and **Try** |

The newest-check card reads the console's deploys of the draft (`GET /api/deploys?workspace_server=&project=&workspace=`), takes the newest one of each
Runner and channel, and asks the console for the newest run of `preflight` there (`GET /api/deploys/{id}/runs?task=preflight&step=preflight-findings`,
[Runs of a deployed build](publishing.md#runs-of-a-deployed-build)). The run may be of an older version than the deploy's: the preflight a deploy ran
before activation, or a later **Re-check**. A Re-check on the Runs tab replaces the card's run.

The card never shows a **Test** of a connection. The Runner answers a test at once and records it in its audit, so it is not a run the console can
find. The steps of a test are shown where **Test** is pressed. A login that fails shows on the card as an error of the next `preflight`, which reads
every connection with the logins the channel binds.

Example: Ana published `crm-to-dw` to `dev` and `prod` of `runners-eu`. The Runs tab shows two cards. The card of `prod` says "preflight of version 3,
succeeded, finished 2026-11-23T08:00:05Z", links the run, and lists "error crm: Change tracking is off on dbo.orders.". The card of `dev` says "No
preflight has run in dev yet."

- **Build**: discovery of source tables into the cache, then import of models from it; its target's state is its `preflight`.
- **Store**: browses nothing; a model's storage connection is tested and checked by its `preflight`.
- **Sync**: [below](#a-sync-project).

## A sync project

**Discovery.** On a new replication's Overview the database pickers list the cache's databases of each connection; a connection with no cache shows
**Refresh catalog** with the channel picker, which runs `discover`. The Table Mappings overview lists the source's cached tables; **Create mappings**
writes a mapping file per ticked table from the cache (columns, types, key; the target's types inferred by Sync's mapping column reader over the cache).
**Refresh** on that overview reads the connection again, on a mapping's cached-metadata card one table, each showing the difference first. **Custom
query, not a table**, then **Describe columns** on the card **Query columns** of Column Mapping, writes the query's columns into the mapping's column
cache (`sourceColumns`: each column's name, type and whether it may be null; a query reports no key).

The pickers, the overview and Column Mapping read the databases, tables and columns from the draft's `catalog-cache.yaml` in the browser
(`catalogCache.ts`), fetched through the Workspace's existing route that reads a draft's file; a refresh not yet saved is held in the browser as an
unsaved edit and read the same way. No server route answers metadata: the server would read the saved file and miss the unsaved edit. Example: Ana
presses Refresh on `accounts`, accepts "1 column added", and opens Column Mapping before saving; the new column is there.

A connection whose driver is Sync's own (a YAML driver, or a `metadataProvider` script) is read by the tasks `discover` and `describe` of the replication
set's own package (Sync's metadata service behind them), which needs a build staged in that channel; the Designer says so and offers **Build and stage
for dev**.

**The source.** Offline: from the cache and the files. Live: as checks of the set's `preflight`, with the run login the replication will use, read-only.

| Check | Offline | Live |
| --- | --- | --- |
| the table and every mapped column exist | against the cache | the catalog read again |
| the filter's columns exist and the filter compiles | the columns the filter names, against the cache; a filter the dialect's tokenizer cannot read is "not checked offline" | a zero-row read `WHERE (<filter>) AND 1 = 0` |
| the login can read the table | no | the engine's privilege function (`HAS_PERMS_BY_NAME`, `has_table_privilege`), then the same zero-row read |
| change tracking, CDC or a logical slot is on where the reader's strategy needs it | no (`unknown`) | the database's and the table's setting, `wal_level`, the slot and the publication; a missing one is a provisioning step of the source side |
| every source type is one the driver reads | the driver's type map over the cached type text | the engine's own type of each column |
| a key for a reader or writer that needs one | the cache's key flags | the same, read again |

**The target.** Nothing is written, ever, to check it.

| Check | Offline | Live |
| --- | --- | --- |
| the table exists, or provisioning can create it | the provisioning plan from the cache, its create or alter statements shown | the plan again from the live catalog, as `provision` plans before it applies |
| the login can insert, update and delete; for provisioning, create in the schema and alter the table | no | the engine's privilege functions on the table and the schema; never a trial write |
| types are compatible with the mapping | each source column's inferred type against the target column's | the same, read again |
| the keys agree (the mapping's key against the target's primary or unique key; no key column nullable) | yes | yes |
| where the replication's state is kept (the `tracking:` schema on the target) | the configured place is named | the schema and tables exist or the login may create them |

**The live findings.** Every Sync package carries the task `preflight` beside `provision`. It reads each mapping of each enabled replication with
the connections of the slots the channel binds, so with the run login a pass uses. Its input `replication` names one replication; empty, the default,
checks them all. A finding's `about` is the connection it was read on.

The preflight writes nothing. It reads the catalogs. It asks the engine whether the login holds a privilege (on SQL Server `HAS_PERMS_BY_NAME`, on
PostgreSQL `has_table_privilege`, `has_schema_privilege` and the table's owner). It runs the read `SELECT <mapped columns> FROM <table> WHERE
(<filter>) AND 1 = 0`. It makes the provisioning plans as `provision` makes them, and never applies them.

How much a live finding weighs:

- Errors, because the first pass would fail on them:
  - a source or target table, or a mapped column, that is not there;
  - a filter or a read of the source that the database refuses;
  - a run login that may not read the source, or may not insert, update or delete on the target;
  - change tracking, CDC, a trigger's shadow table or a logical slot that the reader needs and that is off. The finding carries the statements that
    turn it on, the source side of the provisioning plan;
  - a reader or writer that the connection's driver does not have;
  - a state table on the target that is missing and that the run login may not create.
- Warnings: a type that does not fit or may not fit the target column, a type the driver does not read, a key column that is nullable, a mapping key
  that is not the target's primary key, and a reader or writer that matches rows on a key the tables do not give it.
- Information: what is in order, and what was not checked on this engine.

Example: the replication `orders` reads `crm.dbo.orders` with `MsSqlChangeTracking`. In `prod` change tracking is on for the database and off on the
table, and the run login of `dw` holds only `SELECT` on `dbo.orders`. The preflight of the staged version records:

```
error  crm: The reader 'MsSqlChangeTracking' needs the source crm.dbo.orders to be set up first: Enable Change Tracking on [dbo].[orders].
            The statements that do it (the source side's provisioning, which the set's `provision` task applies):
            ALTER TABLE [dbo].[orders] ENABLE CHANGE_TRACKING WITH (TRACK_COLUMNS_UPDATED = OFF);
error  dw:  The run login of 'dw' may not INSERT into dw.dbo.orders.
error  dw:  The run login of 'dw' may not UPDATE dw.dbo.orders.
error  dw:  The run login of 'dw' may not DELETE from dw.dbo.orders.
```

The console keeps the version staged and held. A DBA enables change tracking and grants the login its rights, and Ana runs **Re-check** before she
activates the version.

**The offline findings.** The Workspace runs the offline checks in its own process when it checks a draft, after Sync's own check of the files. It
reads every table mapping against `catalog-cache.yaml` and the connection files, and reports each finding on the mapping's file and line. Example: the
mapping `orders` maps the column `shipped_on`, and the cache's `crm.dbo.orders` has no such column. The Check tab then shows "The source column
'shipped_on' is not a column of crm.dbo.orders in catalog-cache.yaml (read from runners-eu, channel dev at 2026-11-18T10:00:00Z)" on that line of
`config/replications/orders/table-mappings/orders.yaml`. Refresh the connection, or correct the mapping.

How much a finding weighs:

- A table or a column that the mapping names and the cache does not hold is an error. This covers a mapped column, a column a source filter names
  and the table of a relationship. The check fails, and a build stops until the cache is refreshed or the name is corrected, so a build never carries
  a mapping the cache contradicts.
- A source type that does not fit the target column, a type the driver's type map does not know, and a key that does not agree with the target are
  warnings. The Check tab counts them and a build goes on; the set's `preflight` checks them again on the databases.
- What could not be checked offline (no cache, a connection with no entry in it, a filter the lexer cannot read) is a note.

A type is judged by the same code the Sync engine reads it with: the connection's dialect over the shared type tables (`DbDataKit.Dialect`), or a YAML
driver's type map from its `driver.yaml`. So the check and a run never disagree about what a column's type is.

Example: Ana's cache of `crm` was read in `dev` a month ago. A DBA has since added `shipped_on` to `dbo.orders`, and Ana maps it. Her check fails with
"The source column 'shipped_on' is not a column of crm.dbo.orders in catalog-cache.yaml". She presses **Refresh** on `orders`, accepts "1 column
added", and the check passes. Her mapping also writes `amount` (`decimal(18,2)`) into an `integer` column of the target; the check warns that it "may
not fit", and she can still build and stage.

What is left unchecked offline, and what the check says:

- The draft has no `catalog-cache.yaml`: the check says so once and does not check the mappings against the databases' shape.
- The cache has errors: they are reported with the file, and the mappings are not checked against it.
- A connection has no entry in the cache: the check names the connection once and leaves the mappings on it unchecked.
- A source filter the lexer of the source's dialect cannot read (a string that is not closed, a `;`, a subquery, parentheses that do not match): the
  filter is reported as not checked offline. A filter that can be read has each name that stands for a column compared with the cached table.
- A target table that is not in the cache: the provisioning plan shows the statement that creates it. Its types are compared once the cache holds it.
- A connection whose driver the draft does not hold as `drivers/<id>/driver.yaml` (a compiled driver, or one installed where the set runs): the check
  names the connection once, and its source types and filters are not checked offline. A driver with a `driver.yaml` in the draft has its source types
  read through that file's type map.
- A reader or writer that the connection's built-in driver does not have (a Kind of a driver the project carries, or of one installed where the set
  runs): whether it needs a key is not checked offline. The check says so once per replication.

**A key where the reader or writer needs one.** The check works out which readers and writers each mapping runs, as the engine does: its change
processing, its bulk load, and its reconcile sweep when that is on. It asks each one that matches rows on a key whether the cache gives it one:

- `ChangeApply` and `KeyReconcileDelete` need the target's primary key.
- `TriggerAudit` and `KeyReconcile` need the source's primary key.
- `Scd2` needs a natural key, stated as its `naturalKey` option or derived from the source's key.
- `MsSqlChangeTracking` and `PgLogicalSlot` need the source's primary key: change tracking reports a change by it, and a logical slot reports a
  delete by the table's replica identity, which is the primary key unless a DBA set another.
- `MsSqlMerge` and `MsSqlMergeReconcile` need the target's primary key, with every key column mapped.

Which key a reader or writer needs comes from the built-in drivers' table (`BuiltInDriverCapabilities` in `DbDataKit.Sync.Drivers.Generic`), the
same table the Workspace answers a connection's capabilities from. Example: Ana picks `MsSqlChangeTracking` for `crm.dbo.audit_log`, which the cache shows without a
primary key. The check warns on the mapping's source table: "The reader 'MsSqlChangeTracking' would refuse this mapping: the source table
crm.dbo.audit_log has no primary key in catalog-cache.yaml".

Example: the replication `orders` reads with `Watermark` and names no writer, so it writes with `ChangeApply`. The cached `dw.public.orders` has no
primary key. The check warns on the mapping: "The writer 'ChangeApply' would refuse this mapping: Table mapping 'orders': target table 'public.orders'
has no primary key". Add the key to the target and refresh the cache, or read the whole table each pass with `BatchReload` and write with
`DeleteInsert`, which needs no key.

**The connection test** is each endpoint card's **Test** (above). The old Sync server's test showed up to 20 sample rows; that is not restored. A
mapping's rows are shown by **Preview rows** under the source filter of the mapping's editor, and a segmenting strategy's candidates by **Try** in its
editor ([Rows and trials are answered, never kept](#rows-and-trials-are-answered-never-kept)).

**Where it shows.** The Check tab's count includes the offline findings; the Provisioning sub-tabs show the offline plan and the newest `preflight` run of
the chosen channel (time, run link, findings per mapping, the card **Newest preflight**); the publish panel runs `preflight` on the staged version before activation. Nothing live is
committed into the draft; the cache and a query's column cache are.

Example: Ana maps `crm.dbo.orders` with the change-tracking reader, against `dev`, where change tracking is on, and publishes to `prod`, where it is off on
that table. Staging runs `preflight` in `prod`, which reports "change tracking is off on dbo.orders" with the statement that enables it, before the
version is served; Ana asks the DBA, presses **Re-check**, and activates. Without it the first scheduled pass would have failed at 02:00.

## Example: a new source, end to end

Ana makes the sync project `crm-to-dw` and adds the connection `crm` (engine, address, database, a secret reference: files only). She presses **Refresh
catalog**; the Designer asks which channel to read from. The Runner has no binding of `crm` in `dev`, so it offers **Set up crm for development**: she types
the development login once, the Runner binds it, and `discover` runs in `dev` for Ana, within the schema `dbo` she gave. Four seconds later the tables are
in the pickers and `catalog-cache.yaml` is an unsaved edit of her draft, recorded as read from `dev` on the group `runners-eu`. A week later a DBA adds
`dbo.accounts.segment`; she presses **Refresh** on `accounts`, sees "1 column added", accepts, and the check names the one mapping over that table. At
publish to `prod`, `preflight` checks the same mappings against production's login before the version is served.
