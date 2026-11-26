# dbdatabuild

Builds analytics tables and views on **SQL Server**, **PostgreSQL** and **Microsoft Fabric** from models written once in **DuckDB's SQL dialect**.

It is explicit, offline-first and plan-then-apply:

- A model is a `SELECT` plus a YAML definition that declares its columns. DuckDB binds the query offline and the tool lowers it to one explicit query, then renders the load scripts for each target. The scripts are committed.
- `connection deploy` reads the connection (read-only login), writes a plan file (every statement, its risk, and the reasons), shows it to a person and, when they say so, applies exactly what the plan recorded; it refuses a plan that was edited, goes stale, or has drifted from the connection. `connection refresh` runs the routine loads and never changes structure. `docs/concepts.md` has the picture.
- Every statement to a database goes through one gate with a statement log. The write login is separate from the read login, and nothing falls back from one to the other.
- Several **connections** (named endpoints, each with its engine and version) can be in one project; **mapped** models declare tables the tool does not build, a **copy** moves rows from a table on one connection to another (and from several systems of one application into one table), and **tracking** can be kept on a connection of its own. Nothing is joined across connections and no linked server is used.
- Everything is data you can inspect: every command prints one JSON document (`--format json`) whose shape is a schema in `schemas/`.

> **Status.** Pre-1.0 and not yet used in production. SQL Server 2022 and PostgreSQL 17 are verified by a real-engine test suite. **Fabric has never been run against a real engine**; its support is marked `unverified` throughout.

## Install

Releases carry a single self-contained executable for **Windows** (x64) and **Linux** (x64, glibc 2.34+) as `dbdatabuild-v<version>-<rid>.zip`, with a `SHA256SUMS` file: <https://github.com/DbDataSync/DbDataKit/releases> (this repository holds several products; DbDataBuild's are tagged `dbdatabuild/v<version>`). The install files are the repository's standard ones ([`dist/README.md`](../dist/README.md)); the URLs below never change.

```powershell
# Windows (PowerShell 5.1 or 7)
irm https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/install.ps1 | iex

# Windows, with Scoop (`scoop update` re-reads the manifest)
scoop install https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/dbdatabuild.json
```

```bash
# Linux
curl -fsSL https://github.com/DbDataSync/DbDataKit/releases/download/dbdatabuild/dist/install.sh | bash
```

There is no Windows installer and no service: dbdatabuild is a command line on a project folder and the runtime a Runner starts for a project's tasks
(`--runner-task`). The Build server (`dbdatabuild server`, its screens in the console, its scheduler and its service) was deleted (plan 33 SC23); where each <!-- retired-name-ok -->
of its features went:

| What the server did | Where it is now |
| --- | --- |
| hosted projects; their files, models, graph, checks, compile, sample, import, views, deploy plans | a build project of the Workspace (`dbdataworkspace`), edited in the console |
| ran refresh plans on their schedules (`server run --scheduler`, `server schedule`) | the Runner: the Workspace's publish of the project (or the package `project publish --to runner --out` writes, published from the Runner's Packages screen) and the Runner's own schedule; the runner schedules from the first activation |
| the project's tests, the status of a connection, compare (its Tests, Status and Compare tabs) | the read-only manual tasks `tests`, `status` and `compare` of the refresh package, each recording the command's JSON document as the step `view-result`; the Workspace's Runs tab starts them and shows the document |
| re-checking a model's checks after the data was fixed | the read-only manual task `check` of the refresh package (plan 35 I3a, `CheckJob` over the shared `QualityCheckJob`): every model with checks, or one, evaluated again with the package's read logins and recorded on the Runner, so a block clears without a refresh; the Workspace's Runs tab (Re-check) and the console's Quality page start it |
| applied a plan, acknowledged a block, loaded seeds for a person | the manual tasks `apply_plan`, `ack` and `seed` of the operations package (`project publish --to runner --operations --out`), run on the Runner under the person's name |
| kept the logins (`secret`, `secret send --to runner`) | the Runner: bind each login to the package's `build-login` slot (its Packages page) |
| published data sources and semantic models to a paired Gateway (`server proxy`, `semantic publish`) | the Workspace's publish to a Gateway (the models and data sources `project publish --to gateway --out` writes, no login) |
| `lineage`, `state`, `health`, `service` | the Workspace's Lineage panel and the Runner's lineage (the package carries the project's design graph, `lineage/design.json`); the Runner's state and health |

Each of those commands answers with that sentence and exit code 2 (a clean break, never a silent one).

## A first look

```
dbdatabuild project create adventureworks my-project   # a complete example project (seeds, models, tests) to explore offline; also starter, retail, chinook, fan_in_fan_out (two engines, a copy), macro_examples
dbdatabuild project compile                        # check config and models, lower and lint every query, write the committed load scripts (--check in CI)
dbdatabuild project tests run                      # run the project's tests (metadata rules in tests/metadata/, model tests)
dbdatabuild project sample marts.fct_orders        # run a model on generated sample data, offline
dbdatabuild project show graph +marts.fct_orders   # what a model depends on (also: marts.fct_orders+, --column m.c, --diagram mermaid)
dbdatabuild project import staging.*               # export tables from a connection as mapped models under models/ (diff first; --write to save)
dbdatabuild connection status                      # where the connection stands: drift, blocks, what a deploy would do
dbdatabuild connection deploy                      # plan (asking what a plan must ask), show it, apply it when you say so
dbdatabuild connection deploy --write-plan         # only write the plan file (plans/<connection>/<id>.plan.yml) ...
dbdatabuild connection deploy --apply-plan plans/postgres/<id>.plan.yml --dry-run   # ... and check it, then apply it (without --dry-run)
dbdatabuild connection refresh                     # the routine loads (usually scheduled); never changes structure
dbdatabuild connection monitor                     # what was applied and loaded, and what needs attention
dbdatabuild connection compare marts.fct_orders --against-schema dev   # compare two tables of one connection (counts only; --show-values to see rows)
dbdatabuild ui terminal                            # the same, interactively
dbdatabuild ui mcp                                 # the commands as tools for an AI agent (Claude Desktop, VS Code); also an MCP app
```

`dbdatabuild --help` lists the commands by what they work on (`project`: files, never a database; `connection`: a database) and each command says what it reads and writes (`docs/concepts.md`). `dbdatabuild help code DDB-nnn` explains any diagnostic. `dbdatabuild project agent-kit --write` installs a skill and the JSON Schemas for AI coding agents (`docs/agents.md`).

## Build

Requires the .NET 10 SDK. The SQL parser is a native library built from source once:

```
scripts/fetch-native.sh       # downloads the published SQL library (polyglot-sql, pinned release) into native/<rid>/
dotnet build
dotnet test tests/DbDataBuild.Tests.Unit
scripts/publish.sh linux-x64  # one self-contained executable
```

The real-engine suite uses throwaway SQL Server and PostgreSQL containers (`testenv/testenv.sh up mssql postgres`; see `CLAUDE.md`). linux-x64 is built and tested; win-x64 builds from Linux and has been run under Wine only (the Windows unit job in CI loads the library on real Windows); the SQL library and why it is the published build: `docs/native-library.md`.

## Engines

<!-- engine-support:begin build -->
| Engine | Build target | Build origin |
| --- | --- | --- |
| Arrow Flight SQL | no | yes |
| DuckDB | no | yes |
| Microsoft Fabric warehouse | unverified | unverified |
| Microsoft SQL Server | yes | yes |
| ODBC | no | yes |
| PostgreSQL | yes | yes |

**yes** built and run against a real engine, **unverified** built and never run against one, **planned** not built. Generated from the engine registry; the notes behind each cell are in [engine-support.md](../docs/engine-support.md).
<!-- engine-support:end -->

## Documentation

- `DESIGN.md`: the design. Sections marked "as built" describe what exists.
- `docs/concepts.md`: project and connection, deploy and refresh, plans and events, what each command reads and writes.
- `docs/getting-started.md`: from an offline example project to a build on a database.
- `docs/commands.md`: every command and option (generated from the program; a test keeps it current).
- `docs/operations.md`: running it against real databases, scheduling, failure and recovery.
- `docs/keys.md`: surrogate keys: the 64-bit hash, `keys:` in a model, the generated function, history keys, the unknown member.
- `docs/semantic-models.md`: semantic models in a project: the `semantic/` files, the generated physical layer, the checks, publishing to a Gateway.
- `docs/agents.md`: working with AI agents.
- `docs/interfaces.md`: the terminal, web page, MCP server and MCP app; connecting a host.
- `docs/progress/`: the owner's review summary (`REVIEW.md`) and what is unfinished (`OPEN-ITEMS.md`).
- `docs/research/duckdb-plan-lowering/`: how queries are lowered, with the prototype code.
- `CLAUDE.md`: for anyone (human or agent) changing the code.

## License

MIT. See `LICENSE` and `THIRD-PARTY-NOTICES.md`.
