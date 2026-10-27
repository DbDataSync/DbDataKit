# DbDataKit.Semantic

The semantic layer engine (plan 14, from `Qis.Engine` without its executors). A host gives it a catalog, an `ISourceExecutor` and a clock; it validates a semantic request, plans one scan per source, emits native text (T-SQL, DuckDB SQL, DAX), runs the scans through the executor, combines the result sets in a locked-down DuckDB and returns the result contract. Whatever a level cannot execute comes back as `remaining` steps for an agent to complete.

- **Catalog**: tables, dimensions with attributes, measures, hierarchies, calendars, segments; read from YAML (`Catalog.Load`), with Ossie import and export (`CatalogImport`, `CatalogExport`).
- **Engine**: `QisEngine.Run` takes a JSON document by `kind` (query, native batch, table, bindings, value map, clarify, unsupported) and `Complete` takes an agent completion.
- **Presentation**: `ViewRenderer` turns a result into matrix grids and Vega-Lite specs; it never computes a measure.
- **Schemas**: the JSON Schemas of every document are embedded (`SchemaStore.Get("semantic_request")`).

Not here: the executors (`DbDataKit.Connections` serves them, J03), SSAS (`DbDataKit.Semantic.Ssas`, J16) and spreadsheet extraction (`DbDataKit.Semantic.Extraction`).
