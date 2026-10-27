# Where these documents and the code come from

Imported on 2026-10-27 from `DbDataSync/QueryIntentSpec` at `cddca24` (`qis/docs/` and the rest of `qis/`; J01 and the follow-up that brought SSAS and extraction, plan 19). The documents describe the engine as it was imported, under the names it had there (`Qis.Engine` is now `DbDataKit.Semantic`, `Qis.Contracts` is `DbDataKit.Semantic.Abstractions`, `Qis.Ssas` is `DbDataKit.Semantic.Ssas`; paths inside them still read as in the QIS repository). From here the copy in this repository is the one that changes; where a document and the code disagree, the code and the fixtures are the authority (as `handoff.md` says).

## Every file of the QIS repository, and where it went

| QIS | Here |
| --- | --- |
| `qis/docs/*.md` (11 documents) | `docs/semantic/`, identical; `ssas-ci.md` is new |
| `qis/spec/schemas/*.schema.json` (13) | `DbDataKit/src/DbDataKit.Semantic/schemas/`, identical, embedded in the assembly |
| `qis/spec/third_party/ossie/` | `DbDataKit/tests/DbDataKit.Semantic.Tests/conformance/third_party/ossie/`, identical |
| `qis/dotnet/src/Qis.Contracts` | `DbDataKit.Semantic.Abstractions`; `ISourceExecutor`, `CompiledQuery`, `QueryRows` come from `Qis.Engine/Sources.cs` |
| `qis/dotnet/src/Qis.Engine` (all but the three below) | `DbDataKit.Semantic` |
| `Qis.Engine/Sources.cs` (`DuckDbStandInExecutor`) | the interface to `Semantic.Abstractions`, the row reader to `QueryRowReader`, the stand-in to the tests |
| `Qis.Engine/SqlServerExecutor.cs` | **not imported**: replaced by `DbDataKit.Semantic.Connections` (J03) |
| `Qis.Engine/Extraction/Extractor.cs` | `DbDataKit.Semantic.Extraction` |
| `qis/dotnet/src/Qis.Ssas` | `DbDataKit.Semantic.Ssas` |
| `qis/dotnet/tests/Qis.Conformance/*.cs` (11 files, 503 tests) | `DbDataKit.Semantic.Tests`, all 11, the same count per class; the SQL Server and SSAS switches (`QIS_SQLSERVER`, `QIS_SSAS`) became `EngineConformanceTests` over the shared engines (`DBDATA_TEST_SQLSERVER`, `DBDATA_TEST_POSTGRES`, `DBDATA_TEST_SSAS`) |
| `qis/fixtures/` (321 files), `view-fixtures/` (25), `extraction-fixtures/` (12) | `.../Semantic.Tests/conformance/`, identical |
| `qis/catalog/catalog.yaml`, `qis/data/seed*.sql` | `.../conformance/catalog`, `.../conformance/data`, identical |
| `qis/ssas/model.tmsl.json` | `.../conformance/ssas/` |
| `.github/workflows/ci.yml` | the `semantic-fixtures` job and the shared suite in `dbdatakit-ci.yml` (J02) |
| `.github/workflows/ssas.yml` | the `semantic-ssas` job in `dbdatakit-ci.yml`, by hand ([ssas-ci.md](ssas-ci.md)) |
| `qis/docker/docker-compose.yml` | **not imported**: `testenv/` (SQL Server 2022 and PostgreSQL) replaces it |
| `README.md`, `qis/README.md` | **not imported**: layout and how-to-run text of the old repository; `docs/README.md` is the index of the specs and the layout is in each package's README |
| `.gitattributes` (`eol=lf`) | **not imported**: `DbDataKit/**` is already `eol=lf` here |
| `refengine/`, `run_conformance.py` | **removed upstream** (the Python reference engine and its runner were eliminated in the QIS repository; a local checkout that still has them is out of date). Not imported |

Nothing is shared with the `Qis.*` names any more, so QIS's own CI and ours do not meet.
