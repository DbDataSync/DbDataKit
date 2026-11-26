# DbDataStore

Master data models kept in your own databases: entities, attributes, keys, references and rules written as `model/1` files, validated writes, history and audit, changes to structure by plan and approval (plans 15 and 19, lane Z).
**The Store server was deleted (plan 33 SC23);** the install is four services (console, Workspace, Gateway, Runner), and what the server did is done where the work is:

| What | Where it is now |
| --- | --- |
| Authoring a model (form and YAML, the check as you type) | a `store` project of the Workspace (`StoreModelPanel.tsx`) |
| Its tables: create, expand (dry run first), contract by an approved plan | the Deploy tab of the store workspace, as the Runner tasks `deploy` and `contract` of the model's package, run by `dbdatastore --runner` |
| Rows of an entity: validated writes, history, audit | the Gateway's store module (`GroupStoreApi`), edited on the Rows tab of the store workspace with the `@dbdatakit/store-ui` controls |
| Change requests | the Gateway's store module, decided on the console's Approvals page |
| Publishing a model to a Gateway | the Workspace's publish of the store project (an `entity-model` definition) |

The program `dbdatastore` is that runtime and a command line on a folder of models (`--repo`, by default `DBDATAKIT_DATA_STORE` or the folder `store` under `DBDATAKIT_DATA`). The commands of the server (`server`, `service`, `health`, `state`, `secret`) answer with the sentence that names where their feature went.

```
dbdatastore --runner                                                                          # what a Runner starts for a model's task (deploy, contract)
dbdatastore model put --file <model.yaml> [--name <model>] [--repo <folder>]                  # checked first, then saved
dbdatastore storage create --model <model> [--repo <folder>]                                  # the tables of the model's own copy, on connections/<name>.yaml
dbdatastore gateway add <name> --url <https address> --code <code> [--repo <folder>]          # pair with a serving group as a publishing client
dbdatastore gateway list | remove <name> [--repo <folder>]
dbdatastore model publish <model> [--proxy <name>] [--dry-run] [--repo <folder>]              # its tables, then every group it is published to
dbdatastore model unpublish <model> --proxy <name> [--repo <folder>]
```

## What it is for

Data that people and agents own and change, rather than data replicated, built or queried: reference lists, mappings, hierarchies, targets and budgets, territory assignments, the tables that otherwise live in spreadsheets. A model's tables are kept on SQL Server or PostgreSQL (the engines `ModelStorage` plans for; SQLite, MariaDB, MySQL and Oracle are not supported yet). Sync, Build, the serving layer and semantic queries read them as ordinary tables.

Principles:

- **Master data is ordinary tables on an ordinary engine.** Every consumer reads it with what it already has (a connection, a mapped model, a data source). There is no special read protocol.
- **Writes go through the service; reads do not have to.** Validation, history, approval and audit live in the write path (`DbDataKit.Store`, used by the serving layer's `store` module, plan 20).
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

### Approval of changes

`approval.changes` says whether a write needs a second step. With `none` a person's write is applied directly: change requests are not required. With
`steward` or `owner` a write becomes a change request that a steward (who needs the author role) or an owner (the administrator role) decides; an
agent never decides one, and an agent's write is a change request unless the model says `agents: direct`. Approving your **own** request is not
blocked: the Changes screen says "You proposed this request" beside the buttons and lets you decide it. The reason is that the model, not a global
rule, says how many people a change needs: a model that wants a second person says so with `approval: owner`. For example, a steward who edits `sales_territories` (`approval: {changes: steward}`) sees their own request and may approve it; a
model with `approval: {changes: owner}` waits for an administrator. A rule against approving one's own request would be a further setting of a model,
not a change to this default.

## Tables and structure

`ModelStorage.Plan(model, engine)` makes a schema named like the model, a table per entity (types from the shared dialect, NOT NULL for required and key attributes), primary and unique keys, foreign keys for references inside the model (a reference to another model is checked on write), a `_version` column and the `_audit` table. It only creates: a table already there refuses the whole creation. Statements are `DbDataKit.Schema`'s `TableDdl`, the table-structure planner that Build also uses (structure model, drift classification, risk-classed changes).

A newer version of a model is brought to its tables in two steps (`ModelStorage`): the **expand** step adds new tables and new columns (nullable) and says what it left; the **contract** step plans dropping each column the model no longer has and making each column it requires NOT NULL, answers the statements with a `hash` of exactly that plan, and changes nothing until that hash is approved, then runs that plan in one transaction or nothing. Both run on a Runner as the tasks `deploy` and `contract` below; the Workspace asks the Gateways it publishes to who reads a column before it starts an approved contract (plan 21 CH06).

### Deploying on a Runner

A model's tables are deployed by a Runner (plan 33 SC24; the Store server's own routes for this went with it in SC23). `dbdatastore` is then a runtime:
`DbDataStore/scripts/publish-runtime.sh [rid]` makes the runtime package `dbdatastore-runtime` (the program and the manifest of
`scripts/runtime-manifest.sh`, which starts `dbdatastore --runner`). A model becomes a definition package with
`ModelDeployPackage.Build` (`DbDataKit.DataModel.Runner`): one manual-only task, `deploy`, one connection slot, `storage`,
and the model as `files/model/model.yaml`. A channel binds `storage` to the model's connection on the Runner (the login is the
Runner's). Running `deploy` (`dry_run` is true unless `dry_run=false` is given; `schema` defaults to the model's name) creates
the model's storage when the schema has nothing of it, adds what is missing when it has some (the expand step above), or does
nothing; a change that would remove or retype is left for a contract step (noted in the run's `deploy-report` step) and a
blocked one fails the run without writing. The run is recorded under the name of whoever started it.

The package also has the manual task `preflight`. It reads the schema with the login `deploy` uses and records, as the step `preflight-findings`,
what `deploy` would do (its dry run) and the plan `contract` would make. A change the deploy does not make is an error, because the deploy would fail.
The contract's steps are information and what blocks it a warning, since a person approves the contract on its own. Nothing is written. The console
runs `preflight` on a version it stages in `prod` before it serves it (`docs/publishing.md`, "Preflight before activation").

## Decisions

- The name is DbDataStore; the table-structure planner is `DbDataKit.Schema`, shared with Build.
- History is versioned and effective-dated; application-managed first.
- Reads and writes are served by the serving layer (plan 20); models are authored in the Workspace and their structure is deployed by a Runner (plan 33 SC23, SC24): there is no Store server.
- Instances of a model are separate copies (schema or connection) or shared tables split by an instance id.

## Publishing to the serving layer

The Workspace publishes a store project's models to a Gateway (the four-service way). The command line also publishes a model from a folder, over the Gateway's per-kind routes (until plan 30 PB02 retires them; it is what the apps stories and the compose quickstart use). Pair the folder once with the serving group as a publishing client, with a code its administrator made (`dbdatagateway server pair --max-role publisher`): `dbdatastore gateway add edge --url https://serving.example:7822 --code <code>`.

**Publishing is the one act that makes the model you are working on available** (plan 20 O10b). Editing a model's file changes nothing for anyone; `dbdatastore model publish <model>` (the first time with `--proxy <name>`; after that, to every group the model is published to):

1. brings the model's tables to it: creates them when there are none, otherwise only adds to them (a change that needs more, such as a narrower type, is refused and nothing is published);
2. pins the folder's copy to that version;
3. sends that version to each group, with the write login of the model's connection (kept by the group as its secret, encrypted with its key) and a data source of the same name that refers to it.

A change to rules only (a new `pattern`, a new `max`) is published the same way, with nothing to run on the tables. `--dry-run` lists the statements and the groups and changes nothing. A group that does not take the version (it cannot be reached) keeps the one it had, and the answer says so: the tables and the folder's copy are at the new version, and publishing again sends it. Publishing again with no edit sends the same version, which a group takes as unchanged.

**A published model is read and written in its groups only** (the Gateway's store module). `dbdatastore model unpublish <model> --proxy <name>` takes it off a group. A model with instances cannot be published yet (a group serves a model's own copy only), and its approval settings go with it; permissions per entity are not in `model/1` yet.
