# Overview

## The specs

One shared core and five specs that each own one concern. Each has its own `spec_version` and its own conformance
suite. They connect only through the core's table descriptor.

| Spec | Takes | Produces | Depends on |
| --- | --- | --- | --- |
| Core | none | Table descriptor, names, filter grammar, types, errors, scopes, versioning | none |
| Native query | A question and the catalog | A query intent, compiled to T-SQL or DAX, and a result table with descriptor | Core, catalog |
| Ad hoc extraction | A spreadsheet file | A clean table with descriptor | Core |
| Ad hoc mapping | A table descriptor and the catalog | Column bindings and value maps | Core, catalog |
| Mashup | Descriptors, mapping tables | A combined result table with descriptor | Core |
| Presentation | A result descriptor | Matrix and chart view specs | Core |

In this repository, [catalog.md](catalog.md), [query.md](query.md) and [combine.md](combine.md) cover the
catalog, native query, mapping and mashup specs. The semantic query is the one document an agent writes; the
planner turns it into native queries and combine steps.

## Terminology

These words are used in the dimensional-modeling sense everywhere in the specs, and a name that breaks them is a bug in the name.

| Term | Means | Example |
| --- | --- | --- |
| **Fact table** | A table of events or measurements (one row per sale, per return), with keys to dimensions. Ossie calls tables *datasets* and their columns *fields*; the catalog says table and column | `fact_sales` |
| **Table**, **column** | The physical layer: what a source stores. A fact table and a dimension are tables; an attribute and a measure read columns | `dim_customer.region` |
| **Measure** | An aggregation (sum, count, distinct count, ...) defined over a column of a fact table. The column's values are not "facts"; a measure is what aggregates them | `orders.net_revenue` is `SUM(fact_sales.net_amount)` |
| **Dimension** | A table that describes the context of fact rows: date, customer, product. It has a key, attributes, perhaps roles and hierarchies | `date`, `customers` |
| **Attribute** | A column of a dimension. Attributes are what a query groups and filters by | `date.fiscal_month`, `customers.region` |
| **Role** | One of the ways a fact table relates to the same dimension: the order date and the ship date are two roles of `date` | `ship_date` |
| **Hierarchy** | An ordered chain of attributes of one dimension, coarse to fine | category, then product |
| **Grain** | The set of attributes a result has one row per combination of | region and fiscal month |
| **Source** | Where data comes from: a database or model in the catalog's `sources` | `sqlserver_sales` |

A reference is `dimension.attribute` (and `role.attribute` for a role). So *a dimension is not a column*: `date.fiscal_month` is an attribute of the `date` dimension,
and the catalog, requests and descriptors use these names (`dimensions` holds dimensions, a request has `attributes`, and `x_kind` is `attribute` or `column`); see
[open-questions.md](open-questions.md#terminology).

## Dependency rules

- Native query, extraction and mapping know nothing of each other or of mashup.
- Mashup refers to the others only by reference: a step names a versioned query intent, extraction output or
  registered table, plus its descriptor. It never inspects how a table was produced.
- Presentation reads descriptors and rows and never computes a measure value, including totals.
- Each producer is responsible for the accuracy of its descriptor, especially grain and additivity. Consumers
  trust descriptors and validate against them.
- A plan or view records the version of every spec it used, so results are reproducible.

## Three layers

| Layer | Written by | Contains |
| --- | --- | --- |
| Semantic request | User or agent | Measures, attributes, filters, named transforms, overrides |
| Logical plan | Planner (internal) | Scans at native grain, rollup, spread, union, compute, filter, relations |
| Native queries | Emitters (internal) | T-SQL, DAX and DuckDB SQL: one query per source plus one DuckDB combine |

The agent never writes grains, joins or which engine runs what. The only place an agent writes native code is the
DuckDB completion escape hatch ([core.md](core.md#completing-a-partial-result)).

## The fixed pipeline

**Specified.** The engine always runs these in this order, with no optimizer in early levels:

1. Scan each source (push down only filters and grouping).
2. Apply value maps.
3. Rollup or spread each result set to the requested grain.
4. Union.
5. Compute (ratios, transforms).
6. Add totals.
7. Order and limit.

Each step type compiles to one SQL shape until there is a measured reason to add another.

## Adoption levels

Every request gets the same response shape at every level. Whatever the engine can't execute comes back as
`remaining` steps with `reason_code: level_not_implemented`, which the agent completes with DuckDB SQL. A level
never attempts steps that belong to a higher level.

The design document and the engine currently disagree on where spread sits (see
[open-questions.md](open-questions.md#levels-and-spread)). The table shows both.

| Level | Design document | Engine today | Proposed |
| --- | --- | --- | --- |
| 0 | Native queries the agent writes, wrapped in the result contract | **Implemented** | Same |
| 1 | Single-source semantic queries compiled to T-SQL or DAX | **Implemented** | Same |
| 2 | Union at a shared grain; catalog and fuzzy value maps; filtered measures | Union plus spread (`repeat`, `blank`) and finalize **Implemented** | Union, spread, finalize (as built); filtered measures |
| 3 | Rollup coarser through relations (`sum`, `none`); totals via grouping sets; matrix views | Totals, per-attribute rollup, ad hoc tables with catalog maps and a verified bridge **Implemented** | Rollup (all rules) through relations; ad hoc tables with catalog value maps; join and bridge; totals |
| 4 | Spread finer (`repeat`, `blank`); percent of total; time relations | **Implemented**: fuzzy and LLM value maps, allocate, derived measures, transforms (`pct_of_total`, `share_of_parent`, `per_entity`, `prior_period`, `running_total`, `window`, `index`, chained) | Fuzzy and LLM value maps (**Implemented**); allocate (**Implemented**, including the previous period's weights); transforms `pct_of_total`, `share_of_parent`, `per_entity`, `running_total`, `window`, `index` and derived measures (**Implemented**); time relations (`prior_period`, **Implemented**) |
| 5 | Allocation, rollup and spread overrides, `last` rollup, queries generated from presentation views | **Implemented**: `rank` (with `keep` and `ties`), rollup overrides. Queries generated from views are not built (the views themselves: [presentation.md](presentation.md), **Implemented**) | Rank, rollup overrides (**Implemented**), queries generated from views |

### Bringing a step type into the engine

1. **Specify.** Mark the features and validation codes that belong to the level.
2. **Test.** Add conformance fixtures. Below its level the engine returns the steps in `remaining` and never
   attempts them.
3. **Shadow** (**Implemented**, `shadow: true`). Run the engine's version beside agent completions on real requests; disagreements become fixtures.
4. **Switch.** Once results agree, the engine executes the steps and stops returning them.

Agent completions are saved with their result sets and maps, so every cross-source answer is replayable without the
LLM from level 0 onward, and saved runs become regression tests when a step type moves into the engine.

## Testing strategy

| Layer | What it checks | Status |
| --- | --- | --- |
| Conformance fixtures | Request to result, including schema validity and replay | **Implemented** (`../fixtures`, `dotnet test`) |
| Native T-SQL | The emitted and agent-written T-SQL on a real SQL Server | **Implemented** (`../docker/docker-compose.yml`, `QIS_SQLSERVER`) |
| DuckDB dialect | The same logical query emitted as DuckDB SQL; both dialects return the same rows | **Implemented** for the stand-in; **Proposed** as an explicit cross-dialect check |
| DAX | The emitted DAX text | Text is snapshot-tested only; execution is **Open** |
| Producer contract | Native query and extraction emit descriptors that pass core validation | **Implemented** (the conformance runs validate every result and extracted table against the schemas) |
| Consumer contract | Mapping, mashup, presentation on hand-written descriptors and rows | **Implemented** for presentation (`view-fixtures`, hand-written results); mapping and mashup run on the engine's own datasets |
| End to end | A few full scenarios, such as actuals vs budget by uploaded territory | **Implemented** for one scenario (`EndToEndTests`: a spreadsheet extracted, mapped, queried against SQL Server and SSAS sources with totals, and shown as a matrix); more are welcome |

Until a DAX strategy is chosen, SSAS sources run through the DuckDB stand-in, which executes an equivalent SQL
query against a stand-in model, and the engine reports the DAX it would send. T-SQL and DuckDB are the two native
formats under test.
