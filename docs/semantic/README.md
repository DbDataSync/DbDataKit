# Query Intent Specifications

These documents describe a semantic model format, a semantic query format that an agent can write,
and the rules for combining several data sources, including ad hoc tables and enterprise fact-to-dimension
tables, into one result.

They restate the "Query Intent Specifications" design document (Overview, Core, Native query, Ad hoc mapping,
Mashup and the Concepts draft) with the changes agreed so far, and record what is still open. The engine in
`../dotnet` is the straw man implementation; where this folder and the engine disagree, the status tag says which
is ahead.

## Reading order

| Doc | Covers |
| --- | --- |
| [overview.md](overview.md) | The specs, how they connect, the fixed pipeline, adoption levels, testing strategy |
| [core.md](core.md) | Names, table descriptor, filter grammar, types, errors, result contract, completions, scopes, versioning |
| [catalog.md](catalog.md) | The semantic model: Ossie documents for physical models plus a QIS layer for logical members and semantics |
| [query.md](query.md) | The semantic query an agent writes: measures, overrides, filters, time, transforms, clarify and unsupported |
| [combine.md](combine.md) | Ad hoc tables, bindings, value maps, relations, and the combine stages (union, rollup, spread, join, allocate, compute, totals) |
| [extraction.md](extraction.md) | Spreadsheet to clean tables: the extraction spec, fingerprints and drift, typing, checks against the sheet's own totals |
| [ssas.md](ssas.md) | Running Analysis Services for tests: what was found, what is in the repo, the plan for CI or a VM |
| [presentation.md](presentation.md) | Matrix and chart views of a result: totals read from the result, sorting, column limits, the Vega-Lite subset |
| [open-questions.md](open-questions.md) | Concerns, inconsistencies between source documents, and decisions needed |
| [handoff.md](handoff.md) | For the team taking the code over: repo and code map, acceptance contract, seams, verification state, gaps, first steps |

## Terminology

| Term | Meaning |
| --- | --- |
| Grain | The set of attributes that makes a row unique |
| Rollup | Moving a measure to a coarser grain, by a rule per attribute: `sum`, `avg`, `min`, `max`, `first`, `last`, `none` |
| Spread | Moving a measure to a finer grain than it is defined at: `repeat`, `blank` or `allocate` |
| Relation | A table mapping member values to other member values, with a verified cardinality (value map, bridge, hierarchy, weighted, time) |
| Table, column | The physical layer: what a source stores. The catalog says table and column where Ossie says dataset and field |
| Result set | A table the engine computed or was given, with a descriptor; a result's `result_sets` |
| Step | One item in a result's `remaining` list |

"Regrain" is retired. Earlier documents used it for a step that rolls up or spreads; steps are now named for
what they do, `rollup` or `spread`. The engine, schema and fixtures use the new names.

## Status tags

| Tag | Meaning |
| --- | --- |
| **Implemented** | The engine does this and a fixture in `../fixtures` covers it |
| **Specified** | Defined in the design document, not yet in the engine |
| **Proposed** | New in these docs; needs review before it is built |
| **Open** | A decision is needed; see open-questions.md |
