# Semantic models in a project

A semantic model is the vocabulary people ask questions in: dimensions and attributes, measures, hierarchies, calendars, segments and saved queries, written over the tables the project builds. A model of the project is a table; a semantic model says what "net sales by customer region and month" means over those tables. Build authors it, checks it, and publishes it to a Gateway, which serves it (`docs/semantic/architecture.md` in the repository has the whole path; the formats are in `docs/semantic/catalog.md` and `docs/semantic/query.md`). This page is what Build does.

## The files

- A project's `semantic/` folder holds `semantic/**/*.yml`, one file per semantic model, validated by `schemas/semantic.schema.json` (editors that read the schema complete and check it; the Build workspace edits it as a form and as YAML).
- A file is a catalog **written without its physical layer**. `table:` of a binding and `of:` of a measure name a **model of the project** (`marts.dim_customer`), and `column:` one of that model's declared columns. A binding's key (and a measure's `source:`) names a **connection** of the project; `default` is the project's default connection, so one file serves SQL Server and PostgreSQL alike. The templates `retail` and `calendar` have a working file each (`templates/retail/semantic/sales.yml`).
- Writing `tables:`, or the `kind:` or `dialects:` of a source, is refused (DDB-610): the physical layer is **generated, never written**. For each model and connection Build generates a table (physical name = the model's, columns and logical types from its declaration, key = its `grain`) and instantiates the relationships per connection; the semantic engine's own loader then reads the result, so its validation is the project's.
- **Relationships** come from two places. A semantic file may write `relationships:` between models (many-to-one, `to_columns` are the grain of the `to` model; `x_role` for a dimension reached twice, such as an order date and a ship date). A model may declare `foreign_keys:` (`columns`, `references: {table, columns}`, the same spelling as a mapped model); a key becomes a relationship in every semantic file that uses both models and writes none between them, named like the key. Where a key and a relationship say different joins the compile fails. Roles stay in the semantic file.
- **Measures stay in the semantic files.** Build validates them and does not own them. A source Build does not model (an Analysis Services database, a database nobody imported) may be declared by hand in the semantic file.
- `queries:` in a file are **saved queries** owned by the model: a stored request or semantic SQL with a signature (`parameters`). They are checked when the file compiles (one that does not translate is reported at its own line), and they publish, version and roll back with the model.
- A file may carry `cache:` and `checks:` sections; a file that has either is format 2 of the semantic package (`docs/semantic/catalog.md`).

## What Build checks

| Where | What | How to run |
| --- | --- | --- |
| Compile | The file against the model graph: a renamed or removed model or column breaks the compile at the semantic file's line (DDB-611 unknown model, 612 model not on that connection, 613 unknown column, 614 type disagrees, 615 relationship not many-to-one by the grain, 616 not a source, 617 anything else; DDB-618 for `foreign_keys:`) | `dbdatabuild project compile` |
| Offline | What is bound to what: the sources, the generated tables, relationships (and which came from keys is not yet said), hierarchies | `dbdatabuild project show semantic` |
| Data | One SELECT per hierarchy level pair (a member with two parents), two per relationship (orphan keys; a one side that is not unique), two per declared foreign key and connection | `dbdatabuild connection check`; `dbdatabuild project tests list --kind semantic` lists them |

A calendar is a Build template (`calendar`), not a table kept by hand.

## Publishing to a Gateway

A semantic model is published with a build of the project from the Workspace (a build project's publish, with a Gateway as the target): `dbdatabuild project publish --to gateway --out <folder>` writes what is sent (`gateway.json`, the compiled catalog of each semantic model under `models/`, and the read-only `connection/2` document of each data source it reads under `data-sources/`, with **no login** and no reference to one), offline, and the Workspace sends it over the shared publish API (`PUT /api/v1/definitions/data-source/{name}`, then `.../semantic-model/{name}`).

- The Gateway keeps its own logins for its data sources; nothing of Build's logins reaches it.
- A data source of that name that the Gateway already has is **not taken over silently**: the publish stops and offers the adopt (`POST /api/v1/data-sources/{name}/adopt`).
- The serving policy (`extensions.gateway` of the connection file) and the grants are part of the published definition; the Gateway serves exactly what was published.
- The Gateway loads the package, checks it, writes a new version and switches to it in one step; a package that fails leaves the old version serving. Its **Models** screen lists the versions, serves an earlier one and retires the model.

The Build server, which published a model with the read login of its connection and offered `semantic publish|rollback` on its command line, was deleted (plan 33 SC23).
## What is not built

Published from the Workspace (a build of the project) and not by the command line on its own; rollback is the Gateway's (`POST /api/v1/semantic-models/{name}/rollback`, or its Models screen), not Build's; the `semantic` verbs for agents are mounted in the Gateway and not in Build; the project's metadata documents do not carry the semantic models yet; the checks that every mapped column exists on the Gateway's data source are not built (tables are). The work for these is in [planning/SEMANTICS.md](../../planning/SEMANTICS.md).
