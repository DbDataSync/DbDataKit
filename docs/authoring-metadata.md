# Reading a database while a project is authored

A person who authors a build, sync or store project in the Workspace needs to know what the project's databases hold: which databases, schemas,
tables and columns a connection reaches, what a query returns, whether a connection opens, and whether the login a run will use may do what the
project asks of it. This page says who reads the database for that, with which login, what is kept and where, and what is checked without a
connection at all. It applies to every kind of project.

**Built today:** the catalog cache (`catalog-cache.yaml`, `catalog-cache/1`) filled from a paired Gateway's read-only schema call; the offline readers of the
cache (pickers, a Sync mapping's columns, the target side of Sync's provisioning plan, Build's import of models from the cache); the Runner's slot binding
test of a published version; a staged version's manual tasks (Sync's `provision`, Build's `status` and `check`, the Store's `deploy` dry run and `contract`
plan). **Not built yet:** everything else on this page (the subjects [AUTHORING](../planning/AUTHORING.md) and
[SYNCAUTHORING](../planning/SYNCAUTHORING.md) build it).

## Who reads: the Runner, with the login the channel binds

Every read of a customer database made for authoring runs on the Runner, as a task in a process of its own, with the login a channel binds to the
project's slot: the slot's `read` role where one is bound, else its `default`. The run is recorded as the person who asked. The Workspace and the console
never open the connection and hold no login for it.

Why: the console and the Workspace are Control, which holds no customer-data login ([architecture.md](architecture.md#isolation-by-trust)); the Workspace
also runs authors' build children and script compiles, the last place a login should sit. Reading with the bound login has a second reason: the answer
says what the run will be able to read, not what someone else's login sees. Ana's own login sees `hr.salaries` and the run's does not; a cache made with
Ana's login would offer a table no run can read.

A person never reads with a login of their own. A login a person types is used in one way only: as the login of a slot in a non-production channel, sent
to the Runner once (below). Integrated sign-in (Windows, Entra) cannot be typed or delegated from a browser to a server in any case.

Who may ask: in `prod`, the project's approvers (as for the binding test); in every other channel, its authors.

## The built-in package `dbdatakit-metadata`

The Runner ships a package of its own, `dbdatakit-metadata`, that answers shape only, from `DbDataKit.Metadata`'s readers. It is started for a project's
package, a channel and a slot, resolves the slot through that channel's binding as the binding test does, and refuses a slot the project's package does
not declare. Its tasks:

| Task | Answers | Never |
| --- | --- | --- |
| `discover` | databases, schemas (empty ones too), tables and views, columns with their type text, nullability, primary and unique keys, foreign keys; within the schemas or the name pattern given | a row, a value, a statistic of values |
| `describe` | the columns of a query: the read-only policy (`QueryProbe.RequireReadOnly`) first, then the engine's own describe (`sp_describe_first_result_set` on SQL Server, PostgreSQL's schema-only describe, a wrapped zero-row probe elsewhere) | a row: the query is described, not run |
| `facts` | the server's version and the settings of the database a kind's checks name | a value of a table |

A connection whose driver the built-in package cannot run is read by the project's own package instead, which needs a build staged in that channel.

The Workspace waits up to 10 seconds for the answer; after that it shows the run's link and takes the answer when the run ends.

## Before anything is published: Set up for development

A read needs a binding in some channel, not a published version. When the project's Runner has no binding of the connection's slot in the channel the
person picked, the Designer offers **Set up `crm` for development**: the person types the development login once, the Workspace sends the connection's copy
and that login to the Runner and binds the slot in that channel (the same calls the console's Bindings page makes, as the Workspace's client). The login
is sealed to the Runner group and kept in its store; it never reaches the Workspace's state, audit or log. Only a non-production channel is set up this
way: the Workspace refuses `prod`, whose bindings stay the administrator's. The console's posture page lists the non-production bindings made from the
Workspace.

## The catalog cache

What discovery finds is written into the draft's `catalog-cache.yaml`, an unsaved edit the person commits with the draft, so a build, a review and an agent
read the same thing. The cache records, per connection, where and when it was read: the Runner group and the channel (`{server, product, channel}`), so a
mapping made against `dev`'s shape is checked against `prod`'s at staging (below). The next version of the format, `catalog-cache/2`, adds the databases
and schemas of a connection (empty ones too), foreign keys and that record of where; a file of `catalog-cache/1` is refused naming the file.

**Refresh on request, with the difference shown.** Nothing refreshes by itself. **Refresh** on a connection or on one table reads again in the channel the
cache says it came from; the Designer shows what changed (columns added, removed, retyped) before the cache is replaced, and the draft's check then names
every mapping or model the change touches.

**A query's columns** (**Describe**, a Build model's query or a Sync query source) go where the kind keeps them: a Sync mapping's column cache, a Build
model's descriptor.

**A Gateway data source is a second way to fill the cache.** A connection that is already a data source of a paired Gateway can be read through the
Gateway's schema call (`POST /api/v1/data-sources/{name}/schema`, the Gateway's own read login, no row), as is built; it is the natural source of a Build
model over a served warehouse. Nothing new is built on it, and no data source is made on a Gateway only so that authoring can read it (a Sync target, or an
operational source the Gateway does not serve).

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
| check findings, test steps | the Runner's run record (step outputs, under its retention); a Designer card shows the newest run per channel with its time and link. Not committed: they describe a login at a moment, not the project |
| a row or a value of a table, a sample, a statistic of values (minimum, maximum, distinct values, segment bounds), a login, a connection string, an address | nowhere; the cache's strict reader refuses them by key and by look |

## Testing a connection

**Test** on a connection runs the Runner's slot binding test in a channel: the binding, the connection document, the login's secrets, then a connect
(reach, TLS, login, the server's version), each step said. No row and no sample is shown: a configured test query is run only as the probe, its rows
neither shown nor kept.

A test runs through the Runner in every case. It needs a binding in the channel, not a version: before anything is published it runs in a development
channel once the slot is bound there (Set up for development, above), and after publish in any channel that binds the slot. The binding test reads the
binding, never a staged or served version.

## Checking before production: `preflight`

Each kind's package has a read-only manual task `preflight`: Build's is its `status`, the Store's the `deploy` dry run and the `contract` plan, Sync's its checks of a replication's source and target. The publish panel stages the version in `prod` (staged, not served), runs `preflight` there with production's bound logins, shows its
findings and activates only when the person goes on: a warning shows with its run link; an error refuses activation unless the person confirms, and the
confirmation is recorded. After publish the same task is **Re-check** on the Runs tab.

## Every kind uses the same pieces

The built-in package, the cache, the channel picker, **Refresh**, **Describe**, **Test** and the newest-check card are one implementation
(`DbDataKit.Authoring`, `Shared/web/workspace`) that each kind's Designer and Files area use.

- **Build**: discovery of source tables into the cache, then import of models from it; its target's state is its `preflight`.
- **Store**: browses nothing; a model's storage connection is tested and checked by its `preflight`.
- **Sync**: discovery for its pickers and Create mappings, a query source's columns, and its `preflight`'s checks of the source and the target.

## Example: a new source, end to end

Ana makes the sync project `crm-to-dw` and adds the connection `crm` (engine, address, database, a secret reference: files only). She presses **Refresh
catalog**; the Designer asks which channel to read from. The Runner has no binding of `crm` in `dev`, so it offers **Set up crm for development**: she types
the development login once, the Runner binds it, and `discover` runs in `dev` for Ana, within the schema `dbo` she gave. Four seconds later the tables are
in the pickers and `catalog-cache.yaml` is an unsaved edit of her draft, recorded as read from `dev` on the group `runners-eu`. A week later a DBA adds
`dbo.accounts.segment`; she presses **Refresh** on `accounts`, sees "1 column added", accepts, and the check names the one mapping over that table. At
publish to `prod`, `preflight` checks the same mappings against production's login before the version is served.
