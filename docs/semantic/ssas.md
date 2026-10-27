# Running SSAS for tests

The engine emits DAX for SSAS sources, and the conformance suite has so far run those sources through a DuckDB stand-in, so no DAX has ever been run by a real engine. This document records what
was found about running Analysis Services for tests, what is in the repository for it, and the plan. Findings are marked **checked** (done in this repository's environment), **from sources**
(search results or documentation, not tried) or **unverified**.

## What we need

A **Tabular** instance of Analysis Services with `ssas/model.tmsl.json` deployed to it, and a process that sends it the engine's DAX with ADOMD.NET. Multidimensional mode isn't needed:
the catalog's DAX is `SUMMARIZECOLUMNS` over a tabular model.

## Findings

| Question | Finding |
| --- | --- |
| Where does SSAS run? | Only on Windows, installed with SQL Server setup (`setup.exe /FEATURES=AS /ASSERVERMODE=TABULAR ...`, from sources: [install from the command prompt](https://learn.microsoft.com/analysis-services/instances/install-windows/install-analysis-services)). There is no Linux build and no Linux container |
| Can the client run on Linux? | **No (checked).** `Microsoft.AnalysisServices.AdomdClient` 19.118.3 restores and loads on .NET 10 on Linux, then `AdomdConnection.Open()` throws `NotSupportedException`: "This feature is supported for a .NET Core client only on Windows systems" for a TCP data source, and the same plus "requires that the .NET Core Desktop framework is installed" for an HTTP one. So the process that runs DAX has to be a Windows process, whatever the server is |
| Windows containers? | Not a path. No SSAS image exists that I could find; Microsoft discontinued SQL Server Windows containers in July 2021 (from sources, a trade article, weakly sourced); and a Windows container needs a Windows host, which is no gain over installing on that host |
| Is SSAS on GitHub-hosted runners? | **No (checked).** The `windows-2022` image has no Analysis Services service. The [mssqlsuite action](https://github.com/mwaddell/mssqlsuite) installs `sqlengine`, `sqlclient`, `sqlpackage`, `localdb` and `fulltext`, not Analysis Services, and the runner image does not include it |
| Can it be installed on a hosted runner? | **Yes (checked, run 6).** `choco install sql-server-2022 --params "'/FEATURES=AS /ASSERVERMODE=TABULAR /INSTANCENAME=QIS /ASSYSADMINACCOUNTS=BUILTIN\Administrators'"` takes about 2.5 to 3 minutes. The Developer bootstrapper (SSEI) hung or downloaded nothing in three tries, so use Chocolatey. The named instance is not reachable as `localhost\QIS` unless SQL Browser runs; read the port from `msmdsrv.port.txt` and connect to `localhost:<port>` instead |
| Developer-machine install? | Works the same way, by hand: SQL Server Developer edition with the Analysis Services Tabular feature, instance name `QIS`. Power BI Desktop also carries a local Analysis Services engine on a random port, but it can't be started headless or given a fixed model, so it is no use for repeatable tests |
| A hosted Tabular server? | Azure Analysis Services, or a Fabric or Power BI capacity's XMLA endpoint, can be queried over HTTPS from anywhere, but not with ADOMD from Linux (above): it needs a hand-written XMLA client, an Entra ID service principal, and a paid capacity. I found no retirement notice for Azure Analysis Services, which doesn't make it a good bet for a test rig |
| Can the model need no source database? | **Yes.** `ssas/model.tmsl.json` creates each table with a DAX `DATATABLE` calculated table, so the model carries its own data and deploying it needs only the instance |

## What is in the repository

- `ssas/model.tmsl.json`: a `createOrReplace` TMSL script for database `qis_budget`: tables `Customer`, `Date` and `Budget` with the seed data of the SSAS source, relationships, and the measures `[Budget Amount]` and
  `[Budget Lines]`, so every DAX name in `catalog/catalog.yaml` exists. Compatibility level 1600 (SQL Server 2022). It has met a server: SSAS rejected `defaultPowerBIDataSourceVersion` (removed), and calculated tables need a refresh after deploy (`DeployModel` now sends one).
- `SsasModelTests`: run everywhere, and keep the model from drifting: its rows equal the seed rows of `fact_budget`, `dim_month` and `dim_customer`, every DAX column and measure the catalog names is in it, and each relationship
  points at columns it has. (A change to the seed data without a change to the model fails.)
- `Qis.Ssas` (`SsasExecutor`): sends DAX to a server with ADOMD.NET, sends anything else to a fallback executor, and deploys the TMSL (`DeployModel`). Compiles on every OS; runs only on Windows.
- The conformance suite uses it when `QIS_SSAS` is set: `QIS_SSAS="Data Source=localhost\QIS"` deploys the model once and runs the SSAS source's queries as real DAX, with the same fixtures and the same checks as on the stand-in.
  Set it together with `QIS_SQLSERVER` to run both real engines.
- `.github/workflows/ssas.yml`: a manual workflow that installs AS Tabular on a hosted runner, deploys the model, runs a smoke fixture, then the full suite and groups failures by cause. Six runs so far.

## Plan

1. **Try the hosted runner** (cheap, and it answers the unverified rows). Run the manual workflow. If the image already has Analysis Services, delete the install steps. If not, see how long the install takes and
   whether caching the downloaded media helps. If it works in a reasonable time, run it nightly and on demand rather than on every push, because the SQL Server job stays fast without it.
2. **If it can't or shouldn't run on hosted runners, use a Windows VM** (below) with a self-hosted runner on it, so the tests run *on* the machine that has the server and ADOMD talks to `localhost`. That avoids
   exposing the server and any need for an XMLA client on Linux.
3. **Only if a Linux-only pipeline is required,** write an XMLA-over-HTTP client (a SOAP `Execute` of the DAX statement, rowset XML back) so the test process can stay on Linux and talk to a server on the VM through
   IIS's `msmdpump.dll`. That is real work and a security surface; nothing needs it yet.

### The VM

| | |
| --- | --- |
| Host | Windows Server 2022 (or 2025), 4 vCPU, 8 to 16 GB memory, 60 GB disk. An Azure VM, a Hyper-V guest or a spare machine. Start it for a run and stop it after to keep the cost near nothing |
| Software | SQL Server 2022 Developer edition with only the Analysis Services (Tabular) feature, instance `QIS`; the .NET 10 SDK; git; a GitHub Actions self-hosted runner with the label `ssas` |
| Access | Administrators are Analysis Services administrators; no inbound ports are opened, because the runner polls GitHub and the server is used on `localhost` |
| Data | None to protect: the model holds only the sample numbers |
| Job | `runs-on: [self-hosted, windows, ssas]`, `QIS_SSAS: Data Source=localhost\QIS`, then `dotnet test qis/dotnet/Qis.slnx` |

Scripts to write once there is a machine (named so they can sit in `ssas/vm/`): `01-install-ssas.ps1` (unattended setup from the media, with a log dump on failure), `02-deploy-model.ps1` (deploy `model.tmsl.json`
and run one DAX query as a smoke test), `03-register-runner.ps1` (install and register the runner as a service), `04-update.ps1` (patch Windows and the runner). The experimental workflow's steps are the first draft of the first script.

### Observed (run 6)

The model deployed and the smoke fixture (`level2/01_union_same_grain`) passed as real DAX. The full suite: 287 passed, 77 failed. Causes:

- 61: the calculated tables `Customer` and `Date` held no data (never refreshed). Fixed by a refresh in `DeployModel`.
- about 16: CatalogTests and EngineTests edit `catalog.yaml` text by string replacement, which misses under CRLF on a Windows checkout. Fixed with `.gitattributes` (`eol=lf`).

None of the 77 was a genuine DAX difference.

### Observed (run 7, with both fixes)

345 passed, 19 failed, in 25 seconds of test time (install 3.5 minutes, smoke test 1 minute). Every other fixture, including every SSAS source query that isn't a subtotal, gives the same rows from real DAX as from the stand-in and SQL Server. The 19 failures are two findings:

- **18: `ROLLUPADDISSUBTOTAL` was rejected** (a misspelling, see run 8) ("Failed to resolve name 'ROLLUPADDISSUBTOTAL'. It is not a valid table, variable, or function name", at the position of the first flag column, inside `SUMMARIZECOLUMNS`). These are the native subtotal fixtures (`level3/32`, `35`, `level4/10`, `43`) under each totals mode. The function is documented as valid only as an argument of `SUMMARIZECOLUMNS`, which is where it is emitted, wrapped in `SELECTCOLUMNS`. Not diagnosed yet. Candidates, cheapest first: the engine doesn't accept it when `SUMMARIZECOLUMNS` is nested in `SELECTCOLUMNS` (try `ADDCOLUMNS`, or compute the level in an outer `VAR`); the form `ROLLUPADDISSUBTOTAL ( ROLLUPGROUP ( ... ), name )` is needed; or this build's parser takes the function only with a different argument shape. Until fixed, `totals` on an SSAS source should compute in the engine (`QisEngine.TotalsCompute = engine`) rather than natively.
- **1: a blank row is dropped** (`level2/15_filtered_measure_dax_text`: expected a row per region with an empty West budget for East, got only West). This is the predicted `SUMMARIZECOLUMNS` behaviour: a group whose measures are all blank is omitted. The decision is whether the contract says rows with all-empty measures are optional for SSAS sources (a documented source difference) or the emitter adds them back (an extra attribute-table `ADDCOLUMNS` or `NATURALLEFTOUTERJOIN`).


### Probed (run 8)

`SsasProbe` sent 15 DAX shapes to the server (`QIS_SSAS_PROBE=1`, workflow input `probe=true`).

- **The failures were a misspelling, not the server.** The function is `ROLLUPADDISSUBTOTAL` (two S), and the emitter and the probe wrote `ROLLUPADDISSUBTOTAL`, which no server resolves. The emitter is back to one `SUMMARIZECOLUMNS` with a flag per rolled-up attribute. (The probe's `SUMMARIZE` with `ROLLUP` and `ISSUBTOTAL` did work, but it has no cube form and isn't needed.)
- **Blank rows:** `SUMMARIZECOLUMNS` drops a group only when **all** its expressions are blank, and a filtered measure can be blank for a group that has fact rows (East has 3 budget lines in the quarter but no West budget). `+ 0` returns 0, not blank, and an `ADDCOLUMNS ( VALUES ( ... ) )` form also returns regions with no fact rows (North), which SQL doesn't. The emitter now adds a hidden column `IF ( NOT ISEMPTY ( 'Budget' ), 1 )` for a request-filtered measure, using the model table in the DAX expression of the field the measure aggregates (`fact_budget.amount` has `'Budget'[Amount]` as its DAX expression, so the table is `'Budget'`): it is 1 when the fact table has rows in the group and **blank otherwise**, so a group with facts is kept, and no other combination is. (An expression that isn't blank elsewhere would make SSAS evaluate every combination of the attributes.) Confirmed by run 10.

### Calendar columns in the model

The `Date` table carries the prior-year counterparts (`Fiscal Month Prior Year`, `Fiscal Quarter Prior Year`) beside the month, quarter and year, and `Budget` has a year of earlier rows (FY26-Q1: West 120, 120, 140; East 100, 110, 130), so the SSAS source supports `prior_period`, `window`, `index` and the whole-period relative operators on those levels. The date table's order and each period's days still come from the SQL source (`LevelRanges`), so no DAX reads a calendar. The model has no date column, so a day-level operator or shift (`period_to_date`, `between`, a `calendar_date` counterpart) does not apply to this source. Fixtures `level4/41` and `122` to `127`. Run #12 (2026-10-27, 503 tests) passed on real Analysis Services with these in.

### Result (run 10)

Run #11 (2026-10-27, 497 tests, after the transforms, relative-period operators and renames) also passed with no failures. Run #10: all 365 tests passed against real Analysis Services (SQL Server 2022 Tabular), the same fixtures and checks as on the DuckDB stand-in and SQL Server: native subtotals and cubes with `ROLLUPADDISSUBTOTAL`, HAVING, filtered measures, relative dates, allocate and the rest. The run took 6 minutes (install 3.5, smoke test 1.5, suite 30 seconds). No difference between DAX and the stand-in remains known.

### What to expect on the first real run

The stand-in agrees with SQL Server on every fixture, but DAX has never been run, so differences are likely, and each is a finding worth recording rather than a failure to hide. The ones I can predict:

- `SUMMARIZECOLUMNS` leaves out a row whose measures are all blank, where SQL's grouped query keeps a row for any group. Fixtures that expect a row with an empty budget will differ.
- `ROLLUPADDISSUBTOTAL` is emitted once per listed attribute (every combination of subtotal flags), then filtered to the chain, and for a cube the level is looked up with `SWITCH`. The semantics of several of them
  in one `SUMMARIZECOLUMNS` are from the documentation, not from a server (fixtures `level3/32` and `35`).
- Result column names and types: `SELECTCOLUMNS` aliases should come back as plain names, and `CURRENCY` as a decimal; `SsasExecutor` strips brackets and turns whole doubles into longs, which is a guess.
- `TREATAS` and `FILTER` text for the filters the emitter writes, and ordering: DAX sorts blanks first, so `ORDER BY` differs from `NULLS LAST` in the combine.
- The TMSL itself: calculated-table column properties and `compatibilityLevel` are the likeliest first failure.

## Decisions for you

1. **Where does the VM live** (Azure subscription, an existing Hyper-V host, a spare machine), and who owns patching it?
2. **Is a hosted-runner experiment worth a first try** before the VM, given that it spends Actions minutes on a Windows runner (billed at a higher rate than Linux)?
3. **Is a Linux-only pipeline a requirement?** If not, the XMLA client isn't needed.
