# Models and semantic models: what overlaps and what differs

Written for the D12 decision (`docs/plans/13c-follow-ups.md`: should relationships be declared on models as `references:`?). It is read from `DbDataBuild/schemas/model.schema.json`, `DbDataBuild/DESIGN.md` (sections 5 and 9.11), `docs/semantic/catalog.md`, `DbDataBuild/templates/retail/semantic/sales.yml` and `DbDataBuild/src/DbDataBuild.Semantic/ProjectPhysicalLayer.cs`.

## 1. Two different things

| | **Model** (Build) | **Semantic model** (the catalog) |
| --- | --- | --- |
| What it is | One table or view the tool builds, maps or reads: its columns, how it is loaded, where it lives | A description of what people ask for: dimensions, attributes, measures, hierarchies, calendars, segments, saved queries |
| Question it answers | "What is in this table and how does it get there?" | "What does 'net revenue by customer region and month' mean?" |
| Written in | `models/**/<name>.yml` beside a DuckDB-dialect `.sql` file | `semantic/**/*.yml` (the catalog format, written without its physical layer) |
| Named by | `schema.object` (`marts.dim_customer`), the same on every connection | Logical names people use (`customers.region`, `sales.net_amount`), plus synonyms |
| Lives on | One or more connections (SQL Server, PostgreSQL, Fabric, plus read-only origins) | Sources, and a source *is* a connection of the project |
| Consumed by | Build: plan, apply, check, sample, publish | The query engine (Proxy, Console, agents): translates a request into SQL or DAX on a source |
| Changes when | The table changes | The business definition changes |

One sentence: a model is a physical table, a semantic model is a vocabulary over physical tables.

## 2. How they connect today

The semantic file is written **against** models and never describes tables itself:

* A binding's `table:` and a measure's `of:` name a **model** (`marts.dim_customer`). `column:` names one of that model's declared columns. A binding's key (a measure's `source:`) names a **connection**; `default` is the project's one default connection.
* Build **generates** the catalog's physical layer from the models: a table per model and connection whose columns and logical types are the model's, and whose primary key is the model's `grain`. A model on several connections becomes `model@connection` on each.
* `tables:`, and `kind:` or `dialects:` of a source, are refused if written (DDB-610). The file must not say a second time what the models say.
* The generated catalog goes through the semantic layer's own loader, so its validation is the engine's. Findings are diagnostics at the semantic file's line: 611 unknown model, 612 model not on that connection, 613 unknown column, 614 type disagrees, 615 relationship not many-to-one by the grain, 616 not a source, 617 anything else.
* The dependency is one-way: **renaming a column or a model breaks the semantic file at compile time** (613, 611); a semantic file never changes a model.
* Build also derives **data checks** from the semantic side (`SemanticChecks`): for each hierarchy level pair, every child has one parent; for each relationship, no orphan keys and a unique one side. They run with `connection check`.

## 3. Concept by concept

| Concept | In a model | In a semantic model | Overlap |
| --- | --- | --- | --- |
| **Table** | The model itself: `name`, `kind` (view, full, incremental, copy, mapped, native), `connections` | A catalog `table`, generated from the model | Same thing at two levels; the semantic table is *derived*, not written |
| **Column** | `name`, SQL `type`, `nullable`, `collation`, `trimmed` | A `column` with a logical `datatype`; reached through an attribute or measure binding | The semantic column comes from the model's. Types are mapped (`VARCHAR` is `String`, `BIGINT` `Integer`, `DECIMAL` `Decimal`, `TIMESTAMP` `DateTime`, anything else `Opaque`); a disagreement is DDB-614 |
| **Key** | `grain` (columns that identify an output row); `unique_key` for incremental-by-key; `keys` for surrogate keys; `indexes` | `primary_key` of a catalog table | Generated: `primary_key` is the model's `grain`. A relationship's `to_columns` must be that key |
| **Relationship** | Only on **mapped** models, as `foreign_keys:` (`columns`, `references: {table, columns}`), imported from a table that already exists. A built model has none | `relationships:` in the semantic file: many-to-one `from`/`to` with column lists, optional `x_role` for role-playing dimensions | **The D12 overlap.** The only place a built model's join is stated is the semantic file |
| **Description, title, tags, properties** | On the model and on each column (`title` up to 200 characters, `description` up to 4000, `tags`, `properties`) | `label`, `synonyms` on dimensions, attributes and measures | Two vocabularies for documentation. Nothing copies one to the other |
| **Where it lives** | `connections` (built natively on each) | Source = connection; `default`; `totals` and `datetime_zone` per source | The semantic side reuses the model's connection names. Its source `kind` comes from the connection (D13: `postgres` for a PostgreSQL connection) |
| **Time** | A connection's `timezone` setting and the column types | Hierarchies, calendars and shifts, time zones, `is_time` columns, date tables | Semantic only, apart from the column types underneath |
| **Naming** | One physical name, the same everywhere | Logical names, role names (`order_date`), synonyms | Deliberately different: physical names stay in models and bindings, logical names everywhere else in the catalog |

## 4. Only on one side

**Only models:** `kind` and the load strategy (`loads`, `refresh`, `hooks`), `indexes`, `rewrites`, `lint_ignore`, `renames`, `parameters`, collations and string semantics, the `.sql` body and its lowering, plans and apply, the tracking tables.

**Only the semantic model:** dimensions and attributes (shared across sources), measures with their aggregation and semantics (additive, semi-additive, ratio), formats, hierarchies, calendars and period shifts, segments, role-playing dimensions, members lists, per-source `native` expressions (DAX), saved `queries`, and everything a request is validated against.

## 5. What D12 asks

Today a built model cannot say how it joins to another. The semantic file says it (`sales_to_customer: marts.fct_sales_lines(customer_id) -> marts.dim_customer(customer_id)`). Plan 14's gap table intended otherwise: "a declared `references:` (or `foreign_keys:`) on built models' columns, verified by a generated test, not created in the database." J08 did not build it.

| Option | What changes | Effect |
| --- | --- | --- |
| **A. Leave it.** | Nothing | Relationships stay in the semantic file. One place to look for a semantic join, and nothing but the semantic file says how two models join |
| **B. `references:` on a column of a built model** | Model schema, loaders, JSON Schema, goldens. The semantic loader reads relationships from models as well as from the file | A join is declared next to the column. Build could test it without any semantic file. A rename moves the reference with the column |
| **C. `foreign_keys:` on built models, as mapped models have them** | The same schema change, but one concept in both kinds of model | One spelling in both. Closer to the database's vocabulary, and a built model's `foreign_keys:` would be checked, not created |

Whichever is chosen, these have to be decided:

1. **The relationship's name.** The semantic layer addresses a relationship by name (`sales_to_customer`, and `x_role` selects between several to one table). A reference has no natural name; it would need one, or a rule that derives it.
2. **Roles.** A fact table reaching one dimension twice (order date and ship date) needs the role stated; that is semantic, not physical, and probably stays in the semantic file whatever happens.
3. **`foreign_keys:` versus `references:`.** Two spellings for one concept in two model kinds, unless they merge (option C).
4. **Models on different connections.** A relationship is per connection; a reference on a model that is built on two connections applies to both.
5. **A mapped model's imported foreign keys.** They exist now and nothing reads them for semantics. Under B or C they could feed relationships, which would be new behaviour for old data.
6. **Authority.** If both the model and the semantic file say a join, one must win or the two must agree (a compile error when they differ).

## 6. Recommendation

Option A for now. The current form works for every model kind, including mapped and native models whose tables the tool does not build, and nothing is broken. If relationships should move, take C (one concept, one spelling) with roles left in the semantic file, and do it when many semantic files share the same marts, which is when the duplication starts to cost.
