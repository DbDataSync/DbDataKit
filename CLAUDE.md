# Working in the DbDataKit repository

One repository, four products and the libraries they share. Read the product's own docs before changing it; `DbDataBuild/CLAUDE.md`
lists rules its tests enforce.

| Folder | What it is | Owns |
| --- | --- | --- |
| `DbDataKit/` | the shared libraries (`DbDataKit.*`, packages under `src/`, tests under `tests/`) | what two or more products need: config, connections, dialects, metadata, secrets, hosting, transfer |
| `DbDataSync/` | replication tool: ASP.NET API, React UI (`DbDataKit/web/sync`, an npm workspace), per-run TaskRunner | the replication engine, its drivers, its UI |
| `DbDataProxy/` | Arrow Flight SQL proxy for relational databases | the Flight SQL server, its authentication, the 32-bit ODBC worker |
| `DbDataConsole/` | the `dbdatakit` console host: sign-in, users, invitations (the web app and the servers it manages arrive slice by slice, plan 13 section U) | the console's host and its command line |
| `DbDataBuild/` | CLI that builds tables and views from DuckDB-dialect SQL models | the model language, lowering, targets, plans |
| `dist/` | install scripts, MSI and Scoop generation for every product | packaging |
| `testenv/` | SQL Server and PostgreSQL containers shared by the suites | test engines |
| `scripts/` | repository-wide checks (Python, standard library only) | rules about the repository as a whole |
| `docs/plans/` | the plans; `13-suite-standardization.md` and `13a-suite-slices.md` are the current one | |

A product references a shared library as `<DbDataKitReference Include="Hosting.Cli" />` (its `Directory.Build.targets` turns it into a
project reference in this repository, a package reference otherwise). Do not copy shared code into a product.

## Build setup

- The root `Directory.Build.props` (target framework, language version, nullable), `Directory.Packages.props` (**every** NuGet version)
  and `global.json` (SDK) apply to everything; each product's props imports the root one and adds its own.
- Versions live only in the root `Directory.Packages.props`. A `PackageReference` has no `Version`; the one allowed `VersionOverride`
  is listed in `scripts/check_package_versions.py`.
- `global.json` is read from the nearest folder only. `DbDataKit/` and `DbDataProxy/` have their own copy that adds the Microsoft Testing
  Platform runner (xunit v3); Sync and Build test on VSTest (xunit v2) and use the root one.
- `DbDataKit.slnx` lists every project (`scripts/check_solution.py` keeps it complete); `<Product>.slnf` is each product's filter of it.
- Warnings are errors in every product.

## Build and test

```
dotnet build DbDataKit.slnx                                   # everything
python3 -I -m unittest discover -s scripts/tests              # the repository checks
python3 -I scripts/check_package_versions.py && python3 -I scripts/check_solution.py
```

Per product, from its folder: `dotnet build`, then the suites below. Run the suites a change can affect; CI runs the rest on the pull request.

- **DbDataKit, DbDataProxy, DbDataConsole** (xunit v3, MTP): `dotnet test --project tests/<Name>`. If the test host cannot start (`Handshake failures`),
  run the built executable: `tests/<Name>/bin/Debug/net10.0/<Name>`; `-class` and `-method` select tests.
- **DbDataSync**: `dotnet test --filter "Category!=Integration"` (add `DbDataSync.slnf`); `Category=Integration` needs the engines.
- **DbDataBuild**: `scripts/fetch-native.sh` once, then `dotnet test tests/DbDataBuild.Tests.Unit`. The conformance suite needs engines.

Test engines:

- `testenv/testenv.sh up mssql postgres`, then `eval "$(testenv/testenv.sh env dbdataproxy)"` (also `dbdatakit`); `down` when finished.
- Sync has its own `DbDataSync/docker-compose.yml` (SQL Server source and target, PostgreSQL with `wal_level=logical` and wal2json,
  MySQL, MariaDB, Oracle). Its ports (14330, 14331, 15432, ...) are the tests' defaults. Its Postgres image needs a current
  `postgres:17` base: build it with `--pull`. Both stacks use SQL Server on 14330, so run one at a time.
- Tests with ODBC data sources (Proxy's SQL Server tests, Kit's ODBC projects) need unixODBC and Microsoft's "ODBC Driver 18 for SQL
  Server" installed on the machine.

## Making changes

- Plan 13a splits the standardization into slices, each with a "Done when" test. One slice, one branch `suite/<id>-<short-name>`, one
  pull request; set the slice's status in `docs/plans/13a-suite-slices.md` in the same change. If a slice turns out wrong, change the
  plan in that pull request and say so.
- A clean break is allowed, but never a silent one: a changed file format fails with a message naming the file, and a test says so.
- Commit and push often. Large suites run in CI on the pull request.
