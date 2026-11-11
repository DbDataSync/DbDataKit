# DbDataStore

Master data models kept in your own databases: entities, attributes, keys, references and rules written as `model/1` files, validated writes, history and audit, changes to structure by plan and approval (plans 15 and 19, lane Z).
It is a server with a management API, paired with consoles like the other products; reads and writes of the data itself are served by the serving layer (plan 20).

```
dbdatastore server run [--repo <server directory>] [--listen https://127.0.0.1:8490]
dbdatastore server pair --max-role <roles> [--repo <server directory>]
dbdatastore server clients | unpair <client> [--repo <server directory>]
dbdatastore service install|uninstall|status|start|stop|restart|doctor|unit
dbdatastore state init --topology service|group|install [--engine mssql|postgres --connection <connection with Password=secret:NAME or env:NAME>] [--login <role or user> [--migrate]] [--admin-connection <reference> --apply] [--group <name>] [--settings <file>] [--repo <server directory>]   # writes the state section of server.yaml (a SQLite file per service is the default)
dbdatastore state show [--offline] [--engine mssql|postgres] [--repo <server directory>]      # the state database: engine, where each setting came from, namespaces, module versions, pending migrations; creates nothing
dbdatastore state test [--repo <server directory>]                   # opens it read-only and checks rights, engine version and clashes; stops at the first failure with the fix
dbdatastore state grants --login <database role or user> [--migrate] [--repo <server directory>]
dbdatastore state export --to <directory> [--include-secrets-check] [--repo <server directory>]   # the pairings and pairing codes as a snapshot; stop the server first
dbdatastore state import --from <directory> [--migrate-after true|false] [--repo <server directory>]   # into an empty directory's state
dbdatastore state verify --snapshot <directory> [--db] [--rehash-payloads] [--repo <server directory>]   # the snapshot against itself, or the state against it
dbdatastore state move --to-engine <engine> [--to-connection <string>] [--yes] [--repo <server directory>]   # the server's state is SQLite only for now: it says so and points to export and import
dbdatastore secret set|list|delete|... [--repo <server directory>]   # the server's own secret store (the shared secret verbs): the password a console's state connection refers to is dbdata:console:state:<name> (plan 29b C06)
dbdatastore model put --file <model.yaml> [--name <model>] [--repo <server directory>]      # checked first, then saved
dbdatastore storage create --model <model> [--repo <server directory>]                      # the tables of the model's own copy, on connections/<name>.yaml
dbdatastore proxy add <name> --url <https address> --code <code> [--repo <server directory>]   # pair with a serving group as a publishing client
dbdatastore proxy list | remove <name> [--repo <server directory>]
dbdatastore model publish <model> [--proxy <name>] [--dry-run] [--repo <server directory>]   # make the model as it is now available: its tables, then every group it is published to
dbdatastore model unpublish <model> --proxy <name> [--repo <server directory>]
dbdatastore health check [--url <url>]
```

## What it is for

Data that people and agents own and change, rather than data replicated, built or queried: reference lists, mappings, hierarchies, targets and budgets, territory assignments, the tables that otherwise live in spreadsheets. A model's tables are kept on SQL Server or PostgreSQL (the engines `ModelStorage` plans for; SQLite, MariaDB, MySQL and Oracle are not supported yet). Sync, Build, the serving layer and semantic queries read them as ordinary tables.

Principles:

- **Master data is ordinary tables on an ordinary engine.** Every consumer reads it with what it already has (a connection, a mapped model, a data source). There is no special read protocol.
- **Writes go through the service; reads do not have to.** Validation, history, approval and audit live in the write path (`DbDataKit.Store`, used by this server and by the serving layer's `store` module, plan 20).
- **User data is never broken.** A change to a model's structure keeps its rows, or is refused, or is an explicit approved migration.
- **Agents propose, people approve** where the model says so.

## The model (`model/1`)

A model is a YAML file (`schemaVersion: 1`, strict, read by `DbDataKit.DataModel` through `DbDataKit.Config.Yaml`; a bad file fails naming file, line and column, a newer version is refused with `DBD-110`). A `model/2` file adds the optional `cache:` and `checks:` sections (plan 24a); everything below is the same in both. The server keeps one `models/<name>.yaml` per model in its directory, saved only if valid.

```yaml
schemaVersion: 1
name: sales_territories
title: Sales territories
storage: {connection: mdm_sql}          # connections/<name>.yaml, a connection/1 definition; optional instances: schema | connection | column
approval: {changes: steward, agents: request}   # changes: none | steward | owner; agents: request (default) | direct
entities:
  territory:
    key: [territory_code]
    history: effective_dated            # none | audit | effective_dated
    role: dimension                     # table | dimension | mapping | calendar, for the semantic layer
    attributes:
      territory_code: {type: VARCHAR(10), required: true, pattern: "^[A-Z]{2,4}-[0-9]{2}$"}
      name:           {type: VARCHAR(100), required: true, unique: true}
      region:         {type: VARCHAR(40), references: {model: crm_reference, entity: region}}
      active:         {type: BOOLEAN, default: true}
  territory_share:
    key: [territory_code, region]
    attributes:
      territory_code: {type: VARCHAR(10), references: {entity: territory}}
      share:          {type: "DECIMAL(5,4)", min: 0, max: 1}
    rules:
      - {kind: sum_equals, attribute: share, group_by: [region], value: 1, tolerance: 0.0001}
```

- **Types** are SQL names or the canonical text of `DbDataKit.Dialect` (`string(10)`), written as each engine's own type in the tables. In a one-line `{...}` entry a type with a comma is quoted.
- **Rules of a value**: `required`, `unique`, `default`, `min`, `max`, `pattern`, `allowed`, and `references` (to an entity of this model, or of another model with `model:`). Within a model a reference must name the target's whole key or a unique attribute.
- **Rules across rows**, a closed set: `sum_equals` (a sum per group, with a tolerance), `no_overlap` (two keys of a `group_by` may not hold for overlapping periods; needs `effective_dated`) and `no_cycle` (a hierarchy through an attribute that points to its parent; needs a single-attribute key). A model whose rule cannot be checked is refused at write time (`StoreNotSupportedException`), never silently unchecked.
- **History**: `none`; `audit` (the model's `_audit` table, which every entity has: who, when, the row before and after as JSON); `effective_dated` (each version is a row with `_valid_from` and `_valid_to`, an update from a date closes the open version, an update with no date corrects it in place, a delete ends the row and removes nothing; `GetAsync`, `GetAsOfAsync`, `HistoryAsync`). The baseline is **application-managed**, which works on every engine. Native temporal tables (SQL Server system-versioned) are not built, so there is no second implementation to compare in a conformance suite.
- **Roles for the semantic layer**: an entity can say it is a `dimension`, `mapping` or `calendar`; it is read and validated and nothing consumes it yet (the semantic layer does not read it).
- **Instances** (plan 26): `storage.instances` keeps one copy per instance, as `schema` (`<model>__<instance>` on the connection), `connection`, or `column` (shared tables, `_instance` first in every key). Routes under `/api/v1/models/{m}/instances/{i}/...`; the old paths are the `default` instance.

## Tables and structure

`ModelStorage.Plan(model, engine)` makes a schema named like the model, a table per entity (types from the shared dialect, NOT NULL for required and key attributes), primary and unique keys, foreign keys for references inside the model (a reference to another model is checked on write), a `_version` column and the `_audit` table. It only creates: a table already there refuses the whole creation. Statements are `DbDataKit.Schema`'s `TableDdl`, the table-structure planner that Build also uses (structure model, drift classification, risk-classed changes).

A newer version of a model is migrated per copy with `POST /api/v1/models/{m}/migrate` (administrator, a dry run unless `?apply=true`): it adds new tables and new columns (nullable) and says what it left. Every saved model text is kept under its SHA-256, so a copy runs the version it was made at (`GET .../migrations`: `current`, `behind`, `unknown`). Making a column NOT NULL, dropping or retyping one, and changes to history or instances are not applied (planned changes of their own).

## Writing data

`ModelWriter` applies a batch of inserts, updates and deletes in one transaction: values are read as the type holds them, every rule above is checked (the cross-row rules once per batch on the touched groups, so a batch can move a total between rows), and **every rule broken is reported at once** (rule, entity, attribute, key, a sentence; HTTP 422), writing nothing. An update or delete **names the version it read**; a changed row is a conflict (409, with the current version). Keys do not change. A delete that leaves a row pointing at it is refused. Each change writes an audit record in the same transaction (UUID v7 id, who, the versions, before and after).

`GET .../entities/{e}/rows?limit=&offset=` lists a page, `.../row?key.<a>=<v>[&as_of=|&history=true]` reads one, `GET .../schema` describes the model for forms and typed clients. Roles: authors and administrators write; the token's user is the audit actor.

## Change requests

A write waits as a change request when it is an agent's (a token whose subject starts with `agent:`) and the model does not say `agents: direct`, or when anyone's and the model has `approval.changes` of `steward` or `owner` (`ChangeApproval`). Approving applies the changes as proposed through the same writer; a request they break stays pending. A steward's approval needs the author role, an owner's an administrator, and an agent never decides. Details are under "Change requests" below.

## Published models

Reads and writes of the data itself are served by the serving layer from the models this server publishes to it, so they continue with this server stopped; see "Publishing to the serving layer" below. Structure changes stay here.

## Decisions

- The name is DbDataStore; the table-structure planner is `DbDataKit.Schema`, shared with Build.
- History is versioned and effective-dated; application-managed first.
- Reads and writes are served by the serving layer (plan 20), not by a server of this product; this server keeps models and structure.
- Instances of a model are separate copies (schema or connection) or shared tables split by an instance id.

## The state database

`state.db` in the server's directory (SQLite) unless the `state:` section of `server.yaml` names another engine (plan 29b C07): `state: {engine: postgres, connection: "Host=db;Database=dbdatastore_state;Username=store;Password=env:STORE_STATE_PASSWORD", namespaces: {...}, retention: {days: 30}}`. The first place that names a setting wins: `server run --state-engine ... --state-connection ...` (the `state` verbs take `--engine`), then `DBDATAKIT_STATE_STORE_ENGINE` and `DBDATAKIT_STATE_STORE_CONNECTION` (else the install-wide `DBDATAKIT_STATE_ENGINE` and `DBDATAKIT_STATE_CONNECTION`), then the file, then SQLite. A password is a reference (`secret:` from the server's own store, or `env:`). A setting that is not one is refused naming `server.yaml` and the nearest setting that is; `state show` says where each value came from; `state init` writes this file. A server with no `state:` section behaves as before.

## Publishing to the serving layer

Reads and writes of master data are served by the serving layer (plan 20), from the models this server publishes to it, so they go on with this server stopped. Pair once with the serving group as a publishing client, with a code its administrator made (`dbdatagateway server pair --max-role publisher`): `dbdatastore proxy add edge --url https://serving.example:8815 --code <code>`.

**Publishing is the one act that makes the model you are working on available** (plan 20 O10b). Editing a model's file changes nothing for anyone; `dbdatastore model publish <model>` (the first time with `--proxy <name>`; after that, to every group the model is published to):

1. brings the model's tables to it: creates them when there are none, otherwise only adds to them (a change that needs more, such as a narrower type, is refused and nothing is published);
2. pins this server's copy to that version;
3. sends that version to each group, with the write login of the model's connection (kept by the group as its secret, encrypted with its key) and a data source of the same name that refers to it.

A change to rules only (a new `pattern`, a new `max`) is published the same way, with nothing to run on the tables. `--dry-run` lists the statements and the groups and changes nothing. A group that does not take the version (it cannot be reached) keeps the one it had, and the answer says so: the tables and this server's copy are at the new version, and publishing again sends it. Publishing again with no edit sends the same version, which a group takes as unchanged.

**A published model is read and written in its groups only.** This server refuses its rows, changes and change requests (409, naming the groups), and applying a migration or creating its tables here, so one definition is ever enforced on its tables; a dry-run migration still answers. `dbdatastore model unpublish <model> --proxy <name>` takes it off a group; off every one, this server reads and writes it again. A model with instances cannot be published yet (a group serves a model's own copy only), and its approval settings go with it; permissions per entity are not in `model/1` yet.

## What is served

`/health` and `/version` answer without a token. Everything under `/api` needs a token a paired console issued, within the roles the pairing allowed. So far there is `GET /api/v1/info` (the product, its version and its instance id) and the state endpoints every server has: `GET /api/v1/state` (viewer: the engine, the connection with secrets masked, the modules' versions in the registry against this binary, the migrations pending, where each setting came from and the other live instances) and `GET`/`POST /api/v1/state/maintenance` (viewer reads, admin sets `{"enabled": true, "reason": "..."}`: the server answers 503 with `Retry-After` and the reason to everything but these calls, the health endpoints and an administrator, and reports `idle` when what was in flight has finished; audited as `state.maintenance`, refusals too; nothing is persisted, a restart leaves maintenance). See `Shared/src/DbDataKit.State.Api/README.md`. The Store keeps this audit in `management-audit.log` beside `server.yaml`. `GET`/`POST /api/v1/state/move` (plan 29b C06): the server moves its own state when a console asks, from a frozen server (maintenance and idle), writing the `state:` section of `server.yaml`; the console passes the password as a secret reference the server resolves in its own store (`dbdatastore secret set dbdata:console:state:<name> --stdin`).

## In a container

`DbDataStore/Dockerfile` (build from the repository root: `docker build -f DbDataStore/Dockerfile -t dbdatastore .`) runs the server as an unprivileged user on `https://0.0.0.0:8490`, with `/data` as its directory and a health check. `dist/compose/store.yml` starts it with the console image: the server announces itself on the console's private port and waits to be confirmed (`docker compose -f dist/compose/store.yml exec console dotnet /app/dbdatakit.dll instance confirm dbdatastore`, or in the console's Servers page). `dist/compose/check.sh dist/compose/store.yml dbdatastore` does it all and checks the console lists the server as ready.

## The console's screens

The console shows a Store at `/servers/<server>`: the models, and per model its definition (`/model/<name>`: a form and YAML of the same `model/1` text, the loader's diagnostics as you type, Save quoting the version read, remove for an administrator), its storage (create the tables, migrate by adding) and its change requests (read, approve, reject). Every text that is saved is kept under its version, and a journal beside it (`models/versions/<model>/journal.jsonl`) records who saved it and when (`created`, `saved`, `removed`; a save of the text the model already holds is not an entry; a text kept before the journal has the time its file was written and no author). `GET /api/v1/models/<m>/history` lists the entries newest first (viewer; `current` is the version held now, `exists` is false for a removed model, whose history stays readable) and `GET .../history/<version>` reads one text. The model page's History tab shows them with a diff against the model now; Restore is an ordinary save of that text quoting the version read, so it is checked, journalled and refused as stale like any save. `GET /api/v1/models/<m>` answers the text with `version` (the SHA-256 a save quotes as `PUT ...?expected_version=`; a model changed or removed since answers 409 with `current_version`) and `can` (`save`, `remove`, `admin`: what the caller's roles allow, so a screen offers only that; every route checks again).

## Change requests

A write that must wait for a person is kept as a change request. An agent's write waits unless the model says `approval: {agents: direct}`; anyone's waits when the model has `approval: {changes: steward}` or `owner`. `POST /api/v1/models/<m>/changes` then answers 202 with the request; `GET /requests[?state=pending]` lists them, `GET /requests/<id>` shows one, and `POST /requests/<id>/approve` (or `/reject`, each with an optional `{"note": "..."}`) decides it. Approving applies the changes as they were proposed, through the same writer and its rules; a request they break stays pending. A steward's approval needs the author role, an owner's an administrator, and an agent (a token whose subject starts with `agent:`) never decides. `GET /requests` also answers `can_decide` (whether this caller may decide on this model), `decision_needs` (the role the model's approval asks for) and `approval`, which the console's review screen uses to say why its buttons are off. A person may approve their own request: the Store has no such rule (`approval: owner` is how a model asks for an administrator).
