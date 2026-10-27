# Handoff guide

For the team that takes this code into its own repository and owns packaging, async execution, auth and deployment from then on.
Everything here was checked against the code and docs in this repository at commit `71347ac` (497 tests passing locally; CI run #72 on that commit succeeded). Where a claim could not be
checked it says so. Paths are relative to `qis/` unless they start with `.github/`.

## Start here: if you read an earlier version of this repo

`main` is the version to read; where documents disagree, the code and fixtures are the authority. State at commit `3270992`: 503 tests and about 320 fixtures pass, CI is green on SQL Server 2022 and DuckDB, and the manual SSAS workflow (`.github/workflows/ssas.yml`) passed all 503 tests on real Analysis Services (run #12).

**Renames since the earlier version**

| Earlier | Now |
| --- | --- |
| A column was called a "dimension" (`date.fiscal_month`) | A dimension is a table; a column of it is an attribute. The request key is `attributes`, and the catalog nests `dimensions.<name>.attributes.<attr>` |
| Catalog `datasets` / `fields`, bindings `{dataset, field}` | `tables` / `columns`, bindings `{table, column}`. The Ossie export and import still write `datasets` and `fields` (`dotnet/src/Qis.Engine/OssieShape.cs`) |
| Result `datasets` | `result_sets` (C# `ResultSet`) |
| Filter key `member` | `attribute` or `measure` |
| `time_attributes` with `range`, `date_range`, `granularity` | Removed. Relative periods are filter operators on a period attribute: `last_complete` (with `count`), `current`, `period_to_date`, plus `between` and `equals` |
| Transform entry `{"transform": {"as", "type", ...}}` | Flat: `{"as", "transform": "<kind>", "measure", ...}` |
| `period_to_date` transform | Replaced by `window` (`start`, `end`, `offset`, `output`) |
| `nested_transform` rejected | Transforms chain: a later one reads an earlier one's `as` |
| Error codes such as `unknown_granularity`, `time_attribute_no_calendar` | Renamed or removed, with new codes added; the mapping is in section 5 |

New transforms: `index` and `rank` with `keep`.

**Where integration will bite**

1. **DuckDB.** The engine pins `DuckDB.NET.Data.Full` 1.1.1 and creates connections in three places (`Sandbox.cs`, `Sources.cs`), loading result sets one `INSERT` per row. A connection seam for DuckDB 2.0, and probably Arrow loading, will be needed (section 6).
2. **Executors.** `ISourceExecutor` is the contract. `SsasExecutor` is Windows-only. Schemas load from a folder path and run records go to `runDir`.
3. **Not built:** scope enforcement, auth, extraction-spec saving and versioning, async, and the spec-version and request-hash decisions (section 8).

**Limits to know before testing:** day-level date operators don't apply to the SSAS source (its model has no date column). Windows count members of the date table, not calendar months. Operator names are working names.

## 1. What is being handed over

| Part | Where | What it is |
| --- | --- | --- |
| Specification text | `docs/` | Nine documents with status tags (**Implemented**, **Specified**, **Proposed**, **Open**); see [README.md](README.md) |
| JSON Schemas | `spec/schemas/` (13 files) | The wire contracts: `semantic_request`, `result`, `descriptor`, `table`, `catalog`, `bindings`, `value_map`, `native_batch`, `completion`, `clarify`, `unsupported`, `extraction_spec`, `view` |
| Engine | `dotnet/src/` | Three C# projects on .NET 10 (`net10.0`, `dotnet/Directory.Build.props`) |
| Conformance suite | `fixtures/`, `view-fixtures/`, `extraction-fixtures/`, `dotnet/tests/Qis.Conformance/` | 315 engine fixtures, 25 view fixtures, 12 extraction fixtures, xUnit tests |
| Sample model and data | `catalog/catalog.yaml`, `data/seed.sql`, `data/seed.tsql.sql`, `ssas/model.tmsl.json` | The catalog the fixtures run against, seed data in DuckDB and T-SQL form, and a self-contained SSAS Tabular model |
| CI | `.github/workflows/ci.yml`, `.github/workflows/ssas.yml` | Two automatic jobs and one manual workflow (section 4) |
| Local SQL Server | `docker/docker-compose.yml` | SQL Server 2022 on port 14333 |

The specs say what the formats mean. The engine is the reference implementation and the fixtures are the acceptance test: where the docs and the engine disagree, the status tag says which is ahead.

## 2. Repository map

| Path | Contents |
| --- | --- |
| `/README.md` (repo root) | Entry point: layout table and how to run the tests |
| `README.md` | Layout and how to build and run |
| `docs/` | Specs: `overview`, `core`, `catalog`, `query`, `combine`, `extraction`, `presentation`, `ssas`, `open-questions`, and this file |
| `spec/schemas/` | The JSON Schemas above |
| `spec/third_party/ossie/` | The Apache Ossie JSON Schema, used to check the catalog export |
| `catalog/catalog.yaml` | Sample catalog: two sources (`sqlserver_sales`, `ssas_finance`), tables, relationships, dimensions with attributes, measures, hierarchies, calendars, segments |
| `data/` | `seed.sql` (DuckDB) and `seed.tsql.sql` (SQL Server) |
| `fixtures/level0` to `level5` | Engine conformance fixtures, grouped by adoption level |
| `view-fixtures/`, `extraction-fixtures/` | Presentation and extraction fixtures |
| `ssas/model.tmsl.json` | TMSL script for the sample Tabular model (database `qis_budget`) |
| `docker/` | SQL Server compose file |
| `dotnet/` | `Qis.slnx`, `src/` (3 projects), `tests/Qis.Conformance` |

## 3. Code map

### Projects and packages

| Project | References | Pinned packages (from the `.csproj` files) |
| --- | --- | --- |
| `src/Qis.Contracts` | none | none |
| `src/Qis.Engine` | `Qis.Contracts` | `ClosedXML` 0.105.1, `DuckDB.NET.Data.Full` 1.1.1, `Microsoft.Data.SqlClient` 6.1.2, `YamlDotNet` 16.1.3, `JsonSchema.Net` 7.2.3 |
| `src/Qis.Ssas` | `Qis.Engine` | `Microsoft.AnalysisServices.AdomdClient` 19.118.3 (Windows only) |
| `tests/Qis.Conformance` | `Qis.Engine`, `Qis.Ssas` | `Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2 |

`Qis.Engine.csproj` and `Qis.Conformance.csproj` carry the comment "Versions are from memory; update to the latest stable releases when restoring". They were restored and built, and 497 tests pass, but no one has audited the versions against current releases.

### Files

| File or folder | Responsibility |
| --- | --- |
| `Qis.Contracts/Contracts.cs` | Request and result types: `Filter` (leaf, and, or), `MeasureRef`, `TransformRef`, `SemanticRequest`, `NativeQuery`, `ResultSet`, `Result`, `QisError`, `ResolvedRange`; `QisJson.Options` (snake_case) and `QisJson.RequestHash` (`Contracts.cs:23`) |
| `Qis.Engine/QisEngine.cs` | The engine: `Run` (`:44`) dispatches by document `kind`; request planning, scans (`ScanSource`, `:445`), combine, run records, `Complete` (agent completions), `Replay` (`:844`) |
| `QisEngine.Time.cs`, `.Calendar.cs`, `.Window.cs`, `.Index.cs`, `.Transforms.cs`, `.Hierarchies.cs`, `.FilterValues.cs`, `.AdHoc.cs`, `.Shadow.cs` | Partial classes of `QisEngine`: relative periods and the clock; prior-period shifts; `window`; `index`; transform handling; hierarchies; filter-value checks; ad hoc tables and relations; shadow comparison of completions |
| `Validator.cs` | Schema validation plus request checks (`ValidateRequest`) and the error codes |
| `Catalog.cs`, `CatalogLoader.cs` | The catalog model (dimensions, attributes, measures, tables, calendars, roles) and the YAML/JSON loader with catalog validation codes |
| `CatalogExport.cs`, `CatalogImport.cs`, `CatalogImportPlain.cs`, `OssieShape.cs` | The Ossie boundary: export one Ossie document per source, import them back, import a plain Ossie document, and the table/column to dataset/field translation |
| `Emit/SqlEmitter.cs`, `Emit/DaxEmitter.cs` | Native query text: T-SQL and DuckDB SQL from one emitter; DAX (`SUMMARIZECOLUMNS`, `ROLLUPADDISSUBTOTAL`) |
| `Combine.cs`, `RollupSets.cs`, `DayMap.cs` | The DuckDB SQL that combines result sets (union, rollup, spread, allocate, transforms, totals, order, limit); grouping-set subsets; the day-to-counterpart map a calendar uses to move date filters back |
| `Sandbox.cs` | Loads result sets into a fresh in-memory DuckDB, locks it down, runs combine SQL and agent completions; `Validate` checks a completion with DuckDB's parser |
| `Sources.cs` | `ISourceExecutor`, `CompiledQuery`, `QueryRows`, and the test stand-in `DuckDbStandInExecutor` |
| `SqlServerExecutor.cs` | Production T-SQL executor (`Microsoft.Data.SqlClient`) and `LoadSeed` for tests |
| `AdHoc.cs` | Registered ad hoc tables, column bindings, value maps, fuzzy matching (`Fuzzy`), and the bridges built from them |
| `SchemaStore.cs` | Loads every `*.schema.json` in a folder once and validates documents |
| `TimeZones.cs` | IANA zone handling and wall-clock/instant conversion |
| `Extraction/Extractor.cs` | Spreadsheet extraction (ClosedXML) per the extraction spec |
| `Presentation/` | `ViewRenderer`, `MatrixView`, `ChartView`, `ResultTable`: grids and Vega-Lite specs from a result; never computes a measure |
| `Qis.Ssas/SsasExecutor.cs` | DAX executor (ADOMD.NET), `DeployModel`, `Probe` |

### Request to result

`QisEngine.Run(JsonObject)` (`QisEngine.cs:44`) returns a `Result`. For `kind: query`:

| Step | What happens | Where |
| --- | --- | --- |
| 1. Validate | Schema, then request checks; failures return `status: failed` with error codes | `Validator.ValidateRequest`, `RunQuery` (`QisEngine.cs:126`) |
| 2. Canonicalize | Filter values checked, relative-period operators resolved against the calendar and clock, segments and `having` merged; the request hash is taken from the original document | `ExpandPeriods` (`QisEngine.Time.cs`), `WithSegments`, `WithHaving` |
| 3. Plan scans | Measures grouped by source, shift, window, index and role; transform helper measures scoped onto a catalog copy | `PlanQuery` (`QisEngine.cs:302`) |
| 4. Emit native | One query per scan: T-SQL or DAX, plus a DuckDB twin | `ScanSource` (`QisEngine.cs:445`), `Emit/` |
| 5. Execute | `ISourceExecutor.Execute(CompiledQuery, maxRows)`; datetimes normalized to UTC instants; row cap checked | `QisEngine.cs:459-464` |
| 6. Combine | Union, rollup, spread, allocate, transforms (including chains), totals, post-filters, order and limit, as one SQL text run in a fresh DuckDB | `Combine.BuildSql` (`Combine.cs:116`), `Sandbox.Run` (`QisEngine.cs:583`) |
| 7. Result | `combined` table with descriptor, the `result_sets` that fed it, `remaining` steps, warnings, `resolved` ranges, request hash | `Result` (`Contracts.cs:228`) |
| 8. Run record | A complete result is saved when a run directory is set, so it can be replayed without the agent | `Recorded`, `SaveRun` (`QisEngine.cs:86`, `:820`) |

Other `kind` values: `native_batch` (level 0, agent-written native queries wrapped in the result contract), `table`, `bindings` and `value_map` (registrations), `clarify`, `unsupported`, and `completion` (through `Complete`).

## 4. The acceptance contract

### Fixtures

| Folder | Files | Notes |
| --- | --- | --- |
| `fixtures/level0` | 3 | Agent-written native queries; skipped unless `QIS_SQLSERVER` is set (the stand-in cannot run agent T-SQL) |
| `fixtures/level1` | 80 | Single-source requests, partial results, completions, clarify, unsupported, dates and relative periods |
| `fixtures/level2` | 27 | Spread, union, filtered measures, aliases, snowflake and role-playing dimensions, several fact tables per source |
| `fixtures/level3` | 39 | Totals and subtotals, rollup rules, ad hoc territories through a bridge |
| `fixtures/level4` | 134 | Fuzzy and LLM value maps, allocate, calendars, `prior_period`, `window`, `index`, chaining |
| `fixtures/level5` | 32 | `rank` (ties, `keep`), rollup overrides, post-filters |
| `view-fixtures/` | 25 | Hand-written results and the matrix grids and chart specs they must render to |
| `extraction-fixtures/` | 12 | Workbooks as cells, extraction specs, and the tables or errors they must give |

A fixture is one JSON file: `name`, `level` (the adoption level the engine runs at), `description`, optional `today` (pins the clock), optional `setup` (registration documents run first), `request`, and `expect`. A fixture that needs an agent completion adds `then_complete` and `expect_after_completion`.
`expect` keys checked by `FixtureChecker` (`dotnet/tests/Qis.Conformance/ConformanceTests.cs`): `status`, `reason_code`, `errors`, `errors_include`, `error_suggestions_include`, `warnings`, `result_sets`, `result_sets_absent`, `result_set_covers`, `remaining`, `resolved`, `rows` (with `ordered`), `descriptor`, `native_query_contains`.

### What every fixture checks

1. The result matches `expect`.
2. The result is valid against `result.schema.json`.
3. A complete run replays: the saved run record, replayed in a fresh catalog copy, gives the same rows (`CheckReplay`).
4. Fixtures with `totals` also run in two more subtotal modes (`engine` and `auto`) and must give the same rows, unless the fixture pins where subtotals are computed.

### Test files

| File | Checks |
| --- | --- |
| `ConformanceTests.cs` | The fixtures above, in the modes above |
| `CatalogTests.cs`, `PlainOssieTests.cs` | Catalog validation codes, export/import round trips, plain Ossie import |
| `EngineTests.cs`, `FuzzyTests.cs`, `EndToEndTests.cs` | Engine behaviours outside fixtures, fuzzy scores, one end-to-end scenario (spreadsheet, mapping, SQL Server and SSAS sources, totals, matrix) |
| `ExtractionTests.cs`, `ViewTests.cs` | The extraction and view fixtures |
| `SchemaTests.cs` | Schema files themselves, with negative cases |
| `SsasModelTests.cs` | The TMSL model matches the seed data and the catalog's DAX names (runs everywhere) |
| `SsasProbe.cs` | Not a test: sends DAX shapes to a real server when `QIS_SSAS_PROBE` is set |

### How to run

| Goal | Command |
| --- | --- |
| Everything on the DuckDB stand-in | `cd dotnet && dotnet test Qis.slnx` (SDK 10.0.x) |
| Against real SQL Server | `docker compose -f docker/docker-compose.yml up -d --wait`, then `QIS_SQLSERVER='Server=localhost,14333;Database=qis;User Id=sa;Password=Qis_Passw0rd!;TrustServerCertificate=True;Encrypt=False'` and `dotnet test dotnet/Qis.slnx`. The tests create database `qis` and load `data/seed.tsql.sql` |
| Against real SSAS (Windows only) | `QIS_SSAS="Data Source=localhost\QIS"` (set with `QIS_SQLSERVER` to run both). Deploys `ssas/model.tmsl.json` as `qis_budget` once. See [ssas.md](ssas.md) |
| One fixture | `dotnet test dotnet/Qis.slnx --filter "DisplayName~level2/01_union_same_grain"` |

The tests copy `fixtures/`, `view-fixtures/`, `extraction-fixtures/`, `ssas/`, `catalog/`, `data/` and `spec/` into `bin/.../conformance/` (`Qis.Conformance.csproj`). A new fixture is picked up when the file is added; no test code changes.

### Adding a fixture

1. Copy a neighbour in the right `fixtures/levelN/` folder; number it after the last one.
2. Set `level`, `request` and `expect`. Pin `today` for anything relative to the clock.
3. Work out the expected rows independently (the seed data is `data/seed.sql`), not by copying the engine's output; break one expected value to see the fixture fail.
4. If the fixture adds a new error code or field, update the schema and the doc that owns the concept (section 5).

### CI

| Workflow | Trigger | What it does |
| --- | --- | --- |
| `.github/workflows/ci.yml` job `test` | push to `main`, pull requests | `dotnet test qis/dotnet/Qis.slnx` on Ubuntu with the stand-in |
| `ci.yml` job `test-sqlserver` | same | Same suite with a SQL Server 2022 service container (`QIS_SQLSERVER`), so every SQL Server source query and the level 0 fixtures run as real T-SQL |
| `.github/workflows/ssas.yml` | manual (`workflow_dispatch`) | On `windows-2022`: installs Analysis Services Tabular with Chocolatey, deploys the model, runs a smoke fixture, then the full suite with `QIS_SSAS`, and groups failures by cause. Inputs: `method`, `bootstrapper_url`, `instance`, `probe` |

## 5. Semantics index

### Which document owns what

| Concept | Owner |
| --- | --- |
| Names, table descriptor, filter grammar, types, errors, result contract, completions, scopes, versioning | [core.md](core.md) |
| Catalog format: tables, dimensions, attributes, roles, calendars, measures, rollup and spread rules, hierarchies, segments, Ossie import/export | [catalog.md](catalog.md) |
| The request an agent writes: measures, overrides, filters, relative periods, datetime, transforms (and chaining), clarify, unsupported | [query.md](query.md) |
| Ad hoc tables, bindings, value maps, relations, combine stages, totals | [combine.md](combine.md) |
| Spreadsheet extraction | [extraction.md](extraction.md) |
| Matrix and chart views | [presentation.md](presentation.md) |
| SSAS test setup and findings | [ssas.md](ssas.md) |
| Levels, pipeline order, testing strategy | [overview.md](overview.md) |
| Decisions, open items, transform gaps | [open-questions.md](open-questions.md) |

### Terminology rules

Defined in [overview.md](overview.md#terminology). A name that breaks them is a bug in the name:

- A **dimension** is a table (`date`, `customers`). An **attribute** is one of its columns (`date.fiscal_month`). A reference is `dimension.attribute`, or `role.attribute`.
- A **fact table** is a table of fact rows. A **measure** aggregates a column of one; the column's values are not "facts".
- The physical layer says **table** and **column**. Ossie says dataset and field; the translation happens only in `OssieShape.cs`.
- A query result is made of **result sets**.
- A **member** is a value of an attribute.

### Renames since the earlier documents

Use this to read older notes, fixtures or discussions.

| Before | Now |
| --- | --- |
| Request `dimensions: [...]` | `attributes: [...]` |
| Flat catalog `dimensions` block whose keys were attributes | `dimensions.{dimension}.attributes.{attribute}`; `default_role` is declared once on the dimension; a binding no longer names a role |
| Catalog `datasets`, dataset `fields` | `tables`, `columns`; bindings and a measure's `of` are `{table, column}`; a column that holds times says `is_time` (Ossie nests it in `dimension`) |
| Result `datasets`, `$defs/dataset`, C# `Dataset`, `$.datasets[i]` paths | `result_sets`, `$defs/result_set`, `ResultSet`, `$.result_sets[i]` |
| Fixture keys `datasets_absent`, `dataset_covers` | `result_sets_absent`, `result_set_covers` |
| Filter key `member` | `attribute` (a condition on an attribute) or `measure` (a condition on a measure's value, as in HAVING and `post_filters`); exactly one |
| `time_attributes` with `granularity`, `date_range`, `range` (`last_n_complete`, `current`, `period_to_date`, `member`, `between`) | Filter operators on a period attribute: `last_complete` (with `count`), `current`, `period_to_date`, `between`, `equals`. The breakdown is the attribute in `attributes`. Operator names are working names |
| Transform entry `{"transform": {"as", "type", "measure", ...}}` | Flat: `{"as", "transform": "<kind>", "measure", ...}` |
| Transform `period_to_date` | Transform `window` (`along`, `start`, `end` or `offset`, `output`). The filter operator keeps the name `period_to_date` |
| Descriptor `x_kind: dimension` | `x_kind: attribute` (groupable) or `column` (shown only) |
| Internal `dims`, `dim`, `DimensionDef` for a column | `attrs`, `attr`, `AttributeDef`; `DimensionDef` is the table |
| A transform's `measure` had to be a catalog measure | May also be the `as` of an earlier transform (chaining) |

### Error code changes

| Before | Now |
| --- | --- |
| `unknown_rollup_dimension` | `unknown_rollup_attribute` |
| `rollup_dimension_in_query` | `rollup_attribute_in_query` |
| `totals_order_not_dimension` | `totals_order_not_attribute` |
| `empty_needs_one_dimension` | `empty_needs_one_attribute` |
| `dimension_not_placed` | `attribute_not_placed` |
| `adhoc_dimension_unrelated` | `adhoc_attribute_unrelated` |
| `calendar_unknown_dimension` | `calendar_unknown_attribute` |
| `date_link_unknown_dimension` | `date_link_unknown_attribute` |
| `unreachable_dimension` | `unreachable_attribute` |
| `unknown_dataset` | `unknown_table` |
| `unknown_field` (catalog) | `unknown_column` (the presentation and ad hoc codes `unknown_field` are unchanged) |
| `field_expression_missing`, `field_expression_unsupported` | `column_expression_missing`, `column_expression_unsupported` |
| `measure_dataset_source_mismatch` | `measure_table_source_mismatch` |
| `date_range_invalid` | `between_invalid` |
| `time_dimension_no_calendar`, `unknown_granularity`, `unknown_range_unit` | Removed with `time_attributes`; relative operators on a non-period attribute give `period_operator_not_period_attribute` |
| `period_to_date_totals_unsupported`, `period_to_date_too_many_periods` | `window_totals_unsupported`, `window_too_many_periods` |

New codes: `period_count_invalid`, `period_operator_takes_values`, `measure_filter_misplaced`, `segment_relative_period`, `window_frame_invalid`, `window_period_not_coarser`, `window_attribute_not_coarser`, `index_base_unknown`, `index_attribute_not_coarser`, `transform_base_unknown`, `transform_base_not_scannable`. `nested_transform` is no longer emitted but stays in the `unsupported` schema's reason codes. The full list with meanings is in [core.md](core.md) and [catalog.md](catalog.md).

## 6. Seams the new owner will touch

| Seam | Today | Where |
| --- | --- | --- |
| **DuckDB connections** | Three sites each create their own `new DuckDBConnection("DataSource=:memory:")`: `Sandbox.Validate` (`Sandbox.cs:27`), `Sandbox.Run` (`Sandbox.cs:81`), `DuckDbStandInExecutor` (`Sources.cs:34`). Nothing is injectable. `DuckDB.NET.Data.Full` 1.1.1 is pinned in `Qis.Engine` | `Sandbox.cs`, `Sources.cs` |
| **Loading result sets into DuckDB** | `Sandbox.Run` creates one table per result set and does one parameterized `INSERT` per row (`Sandbox.cs:84-96`), then `SET enable_external_access = false` and `SET lock_configuration = true`, then runs the SQL. A bulk Arrow path is an open item ([open-questions.md](open-questions.md#performance-loading-result-sets-into-duckdb)) | `Sandbox.cs:78-100` |
| **Source executors** | `ISourceExecutor` (`Sources.cs:13`): `Execute(CompiledQuery, maxRows)`, `ExecuteNativeText(source, NativeQuery, maxRows)` for agent-written text, and `RunsNativeText`. Synchronous; no cancellation token | `Sources.cs` |
| **Schemas** | Loaded from a folder path: `SchemaStore.Get(dir, name)` (`SchemaStore.cs:21`), `Validator(cat, schemaDir)` (`Validator.cs:9`), `Catalog.Load(path, schemaDir)` (`CatalogLoader.cs:32`), `ViewRenderer(schemaDir)`. They are not embedded resources | `SchemaStore.cs` |
| **Run records** | `runDir` constructor argument; `SaveRun` writes `{requestHash}.json` with `Directory.CreateDirectory`/`File.WriteAllText` (`QisEngine.cs:823`, `:840`); `Replay(path)` reads a file (`:846`). No storage interface | `QisEngine.cs` |
| **Clock** | `QisEngine.Today` is a settable `Func<DateOnly>` (`QisEngine.Time.cs:9-13`); its default is the UTC date; relative operators read the clock through `TodayFor(zone)`, which uses the pinned date if one is set, else the current date in the request's zone. `Replay` sets it temporarily from the record and restores it (`QisEngine.cs:852-856`) | `QisEngine.Time.cs` |
| **Spec version** | `QisEngine.SpecVersion = "0.3"` (`QisEngine.cs:20`); `semantic_request.schema.json` requires `spec_version` to equal `"0.3"`. No separate version negotiation is coded | `QisEngine.cs`, schemas |
| **Request hash** | First 16 hex characters of SHA-256 over the canonical (key-sorted, compact) JSON (`Contracts.cs:23`). Stable within one implementation; number formatting may differ between serializers | `Contracts.cs` |
| **Ossie boundary** | The catalog does not use Ossie's words; `OssieShape.cs` translates table/column to dataset/field on export and back on import. A field's Ossie `dimension` marker survives only as `is_time` | `OssieShape.cs`, `CatalogExport.cs`, `CatalogImport*.cs` |

### The `ISourceExecutor` contract as the code implements it

The interface has no written specification elsewhere. This is what the engine and the two production executors do today:

| Topic | Behaviour |
| --- | --- |
| Input | A `CompiledQuery`: `Source`, `Native` (`Dialect` `tsql` or `dax`, and `Text`), `DuckDbSql` and `DuckDbParameters` (the DuckDB twin of the same query), and `ExecText` (T-SQL for execution, built with the row limit the engine wants) |
| Parameters | DuckDB text uses positional `?`. T-SQL text uses `@p1..@pN` in the same order as `DuckDbParameters` (`SqlServerExecutor.cs:22-24`, `SqlEmitter.cs:32`). DAX text has no parameters: values are written into the text with quotes doubled (`DaxEmitter.cs:16`, `:23`) |
| Output | `QueryRows(Columns, Rows)`: column names and a list of dictionaries. Whole numbers as `long`, a date column as `yyyy-MM-dd` text, `HUGEINT` as `long` or `decimal` (`DuckDbStandInExecutor.Normalize`); `SsasExecutor.Plain` turns whole doubles into `long` and midnight `DateTime` into a date string. Column names are plain, without DAX brackets |
| Datetimes | The engine, not the executor, converts a datetime attribute to an ISO 8601 UTC instant (`NormalizeDatetimes`, `QisEngine.cs:64`), using the source's `datetime_zone` from the catalog. Executors return what the source stores |
| Row cap | The engine passes `maxRows` (`limit + 1`, or `rowCap + 1`, default 1,000,000: `QisEngine.cs:18`, `:454`); executors stop reading at `maxRows`. More than `rowCap` rows with no limit returns `row_cap_exceeded` (`QisEngine.cs:462-464`) |
| Timeouts | `SqlServerExecutor` and `SsasExecutor` take `commandTimeoutSeconds` (default 60). `SsasExecutor.DeployModel` uses 300 seconds. The engine sets no overall timeout |
| Credentials | The executors receive a connection string in their constructor and open a new connection per query. The engine and catalog never see credentials. The catalog's `sources` hold only `kind`, `dialects`, `totals` and `datetime_zone` |
| Fallback | Both executors take a fallback executor and send anything that isn't their dialect to it, so one executor can serve a catalog with SQL Server and SSAS sources |
| Agent text | `ExecuteNativeText` runs agent-written text (level 0). The stand-in throws `NotSupportedException`; `SqlServerExecutor` runs `tsql` only |
| Async | None. All execution is synchronous |

### Executor status

| Executor | State |
| --- | --- |
| `DuckDbStandInExecutor` | Test stand-in. Loads a seed script and runs the DuckDB twin. Used by default in tests and for the SSAS source when no server is configured |
| `SqlServerExecutor` | Runs the full suite against SQL Server 2022 in CI. `LoadSeed` is a test helper |
| `SsasExecutor` | Windows only: ADOMD.NET throws `NotSupportedException` on Linux (checked, [ssas.md](ssas.md)). Its own comments call its handling of results a draft that has met a real server only in the manual workflow |

### State and thread safety

Read from the code; there is no concurrency test.

| Fact | Evidence |
| --- | --- |
| A `QisEngine` holds per-session state: registered tables, bindings and maps (`_registrations`), pending completions (`_pending`), shadowed results (`_shadowed`), `ShadowReports`, `UnsupportedCounts`, and per-run `_resolved` | `QisEngine.cs:33-37`, `:42`, `:97`; `QisEngine.Time.cs:26` |
| A run changes the `Catalog` while it executes and restores it afterwards: `WithZone` (`Catalog.cs:289`), `ScopedMeasures` (`:402`), `WithRoles` (`:451`) | `Catalog.cs` |
| `Replay` and `Today` assignment mutate the engine | `QisEngine.cs:852-856` |
| `Catalog.Clone()` (`Catalog.cs:271`) gives an independent copy; the tests make one per fixture because ad hoc registrations are added to the copy | `ConformanceTests.cs` |
| The schema cache is static and safe: a `ConcurrentDictionary` plus a lock around schema evaluation | `SchemaStore.cs:13`, `:35` |
| Each `Sandbox` call opens its own DuckDB and disposes it | `Sandbox.cs` |

So: one engine and one catalog copy per session, never shared across threads or concurrent requests. A host that wants concurrency creates an engine per request or per session from a cloned catalog.

## 7. Verification state

| Area | State | Source |
| --- | --- | --- |
| Local suite on the stand-in | 497 tests pass (run for this document) | `dotnet test Qis.slnx` |
| CI on `main` | Run #72 (`71347ac`, the latest commit when this was written) succeeded: both `test` and `test-sqlserver` | GitHub Actions |
| Real SQL Server 2022 | Every engine fixture, the level 0 agent-written queries and the subtotal modes run as real T-SQL in `test-sqlserver` on every push | `ci.yml` |
| Real SSAS Tabular, last full run | Manual workflow run #10 (2026-10-25, commit `32a66e6`): all 365 tests passed on real DAX | [ssas.md](ssas.md), GitHub Actions |
| Real SSAS, since then | Manual workflow run #11 (2026-10-27, commit `71347ac`, after `window`, `index`, chaining, `rank` `keep`, relative-period operators and the renames): **all 497 tests passed, 0 failed**, in 4 minutes (install 2, smoke test 0.5, suite 21 seconds). The suite runs every fixture whose source is the SSAS model as real DAX. The new transforms run in the DuckDB combine over the rows a source returns, so a pass shows they didn't break the DAX scans and the deployed model; it is not a separate proof that each new transform is right over DAX rows, and `window`, `index` on an SSAS source still report `calendar_unavailable` |
| Real SSAS, with calendar columns | Manual workflow run #12 (2026-10-27, commit `7a79926`, model with prior-year columns and a year of earlier budget rows): **all 503 tests passed, 0 failed**, including the six new SSAS-source fixtures (`level4/122` to `127`) and `level4/41` as real DAX scans |
| `window`, `index`, `prior_period` and whole-period relative operators on SSAS | Work for the model's fiscal month, quarter and year, with prior-year counterparts for month and quarter (fixtures `level4/41`, `122` to `127`; the model has no date column, so day-level operators don't apply to it). A datetime bucket with no date table behind it, or a source without the columns, reports `calendar_unavailable` |
| DAX execution outside the sample model | Only `ssas/model.tmsl.json` has ever been queried. A Power BI or Fabric semantic model over XMLA has not been tried |
| Real-time pass-through of source queries from DuckDB | Not built; discussed only |

## 8. Known gaps and undecided decisions

### Gaps

| Gap | Detail | Owner doc |
| --- | --- | --- |
| Scope enforcement | Scopes (`session`, `personal`, `shared`, `governed`) are stored; promotion rules and visibility are not enforced. On hold | [core.md](core.md#scopes), [open-questions.md](open-questions.md#known-gaps-in-the-ad-hoc-slice) |
| Saved extraction specs | Saving, versioning and review of shared specs are not implemented | [extraction.md](extraction.md) |
| Auth and identity | Nothing in the engine knows the caller. Credentials are per executor connection string; row-level security is the source's concern | This document |
| Performance | One `INSERT` per row when loading result sets into DuckDB; an Arrow bulk path is an open item | [open-questions.md](open-questions.md#performance-loading-result-sets-into-duckdb) |
| `index` base `"first"` | Only a named member is supported | [open-questions.md](open-questions.md#transform-gaps) |
| "Other" bucket | Rolling the rows dropped by `rank` `keep` into one row is not built | [open-questions.md](open-questions.md#transform-gaps) |
| `index` with a zero base | No fixture: the seed data has no zero base. The code divides by `NULLIF(base, 0)` | `QisEngine.Index.cs`, `Combine.cs` |
| Unknown `output` on `window` | Caught only by the schema; no fixture expects a schema failure | `semantic_request.schema.json` |
| Rank reading a rank with `keep` | Documented as seeing all rows; no fixture pins it | [query.md](query.md) |
| Date-table gaps | `window` and `offset` count members of the date table, not calendar periods; a sparse date table gives surprising neighbours | [query.md](query.md) |
| DAX and SSAS | SSAS runs only in the manual Windows workflow; `SsasExecutor` is Windows-only and a draft | [ssas.md](ssas.md) |
| Explicit `join` stage, hierarchies as relations | Specified, not implemented (joins happen inside bridges) | [combine.md](combine.md) |
| One key per bindings document | Several catalog attributes from one table need several documents | [open-questions.md](open-questions.md#known-gaps-in-the-ad-hoc-slice) |
| `x_decimal` | Specified, not implemented | [core.md](core.md) |

### Undecided

| Decision | State |
| --- | --- |
| Spec versions | Extraction, mapping and mashup `0.2`, native query `0.1`, the engine emits and the request schema requires `0.3`. No decision; proposed: one version per document shipped from here, bumped together |
| Request hashes across implementations | Stable within this implementation only. Needs canonical number formatting if a second implementation must agree |
| Operator and transform names | `last_complete`, `current`, `period_to_date`, `window`, `index` and the rest are working names |
| `x_` prefix for the catalog's own extensions (`semantics`, `synonyms`, `roles`, `format`, `members`, ...) | No preference; left unprefixed |
| Scope enforcement | On hold |
| Cube compatibility | Optional; the engine emits SQL directly |

## 9. Recommended first steps

1. Run the suite as is (`dotnet test`) and with SQL Server (`docker compose` and `QIS_SQLSERVER`) to confirm the baseline in your environment.
2. Re-run `.github/workflows/ssas.yml` after any change to the model, the DAX emitter or `SsasExecutor` (last run: #11, green, 497 tests).
3. Decide the DuckDB connection seam first (section 6): the three `new DuckDBConnection` sites, which DuckDB version and extensions you ship, and where an Arrow load or pass-through query would plug in. Everything that touches result sets goes through `Sandbox.Run` and `Combine.BuildSql`.
4. Write down the executor contract (section 6) as your own interface, add `CancellationToken` and async, and keep the conformance fixtures as the check that nothing changed.
5. Audit and pin package versions; split extraction (ClosedXML) and the SQL Server executor from `Qis.Engine` if your host does not need them. `Qis.Ssas` is already separate.
6. Make the schemas available without a folder path and decide what is public API; today almost everything is public (82 public type declarations counted, 12 internal members).
7. Put run records behind a storage interface and the clock behind your time source.
8. Decide the open items in section 8 that your side owns: spec versions, request hash portability, scope enforcement and identity.
9. Keep `fixtures/` and `spec/schemas/` as the shared contract: any change to a request, result or code starts with a fixture.
