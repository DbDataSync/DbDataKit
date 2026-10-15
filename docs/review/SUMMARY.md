# DbDataKit Review — Summary

Scope: DbDataSync, DbDataProxy, DbDataBuild (submodules at the commits pinned in this repo).
Method: static review by one reviewer per repo, plus cross-repo synthesis. Nothing was built, run, or
dependency-audited (`dotnet list package --vulnerable` / `npm audit` were not run). Two High findings were
spot-checked by hand; the rest rest on the per-repo reports in `detail/`.

Detail docs: [OVERLAP-AND-CONSOLIDATION.md](OVERLAP-AND-CONSOLIDATION.md) · [SECURITY.md](SECURITY.md) ·
per-repo: [DbDataSync](detail/DbDataSync.md), [DbDataProxy](detail/DbDataProxy.md), [DbDataBuild](detail/DbDataBuild.md)

## The three projects in one line each

| Project | What it is | Definitions | Secrets | Install/update |
|---|---|---|---|---|
| DbDataSync | .NET 10 / ASP.NET / React replication tool; per-run TaskRunner process | YAML in a git repo (auto-commit) | `ClrKernel.Core.Secrets` (OS keyring, env fallback) | NuGet dotnet tool, own updater |
| DbDataProxy | .NET 10 Arrow Flight SQL proxy for SQL Server / DuckDB / ODBC (32-bit ODBC worker) | one `appsettings.json` | plaintext in connection strings | ps1 scripts, Windows service, Docker |
| DbDataBuild | .NET 10 CLI: DuckDB-dialect SQL models → tables/views on SQL Server / Postgres / Fabric, plan-then-apply | strict YAML | env vars `DBDATABUILD_<CONN>_<READ\|WRITE>` | CLI release + native Rust lib |

## Headline recommendations

1. **Do not stop at ODBC metadata. Extract a small set of shared packages**, in this order of payoff:
   (a) dialect/type system, (b) connection + driver descriptor + login builders, (c) secrets,
   (d) strict YAML reader, (e) ODBC metadata (already in flight), (f) transfer/staging, (g) installer/updater.
2. **Pick one secrets model now** and put it in the common repo. Today there are three (keyring, env-var
   per connection, plaintext), and Proxy's is the weakest.
3. **Make DbDataProxy the ODBC/32-bit boundary and have Sync and Build consume it over Flight SQL.**
   Build already has a `FlightSqlLogin`; Sync added ODBC directly via `GenericDriver`. Two ODBC paths will drift.
4. **One provider interface per engine** (quoting, types, DDL, bulk load, metadata). Build splits this across
   `ITarget`, `DialectProfile`, `TrackingDdl`; Sync has `SqlDialect`/`CanonicalType`; Proxy has its own
   provider layer plus Arrow mapper. This is the biggest overlap.
5. **Adopt a shared repo baseline**: central package versions, SHA-pinned Actions, `permissions:` blocks,
   vulnerability scan in CI, non-root containers, one release pattern.
6. Fix the High security items below before consolidating, so shared code doesn't inherit them.

## Security — top findings

| Sev | Repo | Finding |
|---|---|---|
| High | Sync | Library install takes unvalidated `PackageId`/`Version` → MSBuild injection into generated csproj and path traversal via `Directory.Delete` (`LibraryInstaller.cs:199`) |
| High | Proxy | "Read-only" is not enforced on the query path (`AllowWrites` gates only updates); on DuckDB sources any authenticated user gets host file read/write |
| High | Proxy | Pass-through auth accepts any credentials with no throttling, relaying password guessing to the database |
| High | Proxy | Self-signed TLS key written with empty password and default ACLs |
| Med | Proxy | Service runs as LocalSystem, container as root; no concurrency/row/byte limits; command timeout defaults to none; DB error text returned to clients |
| Med | Sync | Unvalidated driver ids / file names in paths; `ConnectionConfig.Properties` skips the credential check and is readable by Viewers; `TrustServerCertificate=true` default; network-trust auth unsafe behind a same-host proxy |
| Med | Build | `token_command` executes an arbitrary program from the Flight SQL login value; native-lib checksum shipped in the same release as the lib; mutable Action tags and release job pushing to `main`; regex `ReadGuard` mishandles Postgres `E''` strings; hook risk labels are author-declared |
| Low | all | Unhashed session ids (Sync), unkeyed plan hash and unredacted statement-log parameters (Build), no vuln scanning anywhere, root containers |

No hardcoded production credentials found; only throwaway test-container passwords.
Positives: Sync auth is closed by default and YAML is typed; Build's YAML reader is strict (no aliases, no
deserializer), identifier quoting and DuckDB sandboxing checked sound; Proxy package versions are current.

## Open caveat on ODBC in Sync

Commit `1543502` reuses `GenericDriver` for ODBC. `System.Data.Odbc` binds parameters by position and nothing
translates the readers' `@name` markers, so parameterised statements will likely fail; only unit tests exist.
Proxy's parameter-binding layer already solves this, which is another argument for consuming ODBC via Proxy.
