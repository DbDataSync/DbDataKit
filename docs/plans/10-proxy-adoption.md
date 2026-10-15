# Plan 09: Adopting the Shared Libraries in DbDataProxy

Status: proposed. Scope: hub CI and publishing of `DbDataKit.*`, how Proxy depends on them, and the order in which
Proxy adopts each one. **Out of scope: the update system** (`DbDataKit.Update.*`, `dbdata-updater`, signed manifests,
side-by-side service swap). Proxy keeps `scripts/install.ps1` and `release.yml` as they are; see "Deliberately skipped".

Inputs: `docs/review/detail/DbDataProxy.md`, `docs/review/SECURITY.md`, plans 01-07, `docs/plans/STATUS.md`.
Finding IDs (H2-H4, S-1, S-2, S-6, S-9, S-10) are the ones used in those documents.

## 1. Where Proxy stands (as of `293362e`)

| Area | Today | Size |
|---|---|---|
| Projects | `DbDataProxy.Core` (client, Arrow writer, executors, worker protocol), `DbDataProxy.Server` (Flight server, auth, config, TLS, hosting), `DbDataProxy.Worker` (win-x86 ODBC host), `DbData.Odbc.Metadata` (local copy of the first shared package) | about 4,100 lines of product code |
| Config | `appsettings.json` section `Proxy`, bound to `ProxyOptions` (PascalCase keys), no validation, no unknown-key detection, no version | `Configuration/ProxyOptions.cs` |
| Secrets | plaintext connection strings, `CertificatePassword`, JWT settings in `appsettings.json`; PBKDF2 `PasswordHasher` of its own | `Auth/PasswordHasher.cs` |
| Providers | `IDbProvider` with `SqlServerProvider`, `DuckDbProvider`, `OdbcProvider`; pass-through via per-provider `ApplyCredentials` | `Data/Providers.cs` |
| SQL handling | `SqlMarkers.Scan` (`?` to `@pN`), no statement classification; `AllowWrites` is not enforced on text | `Core/Data/QueryParameter.cs` |
| Types | `ArrowColumnWriter` if-chain | `Core/Data/ArrowColumnWriter.cs` |
| Metadata | `ProxyFlightServer.Metadata.cs` (524 lines), `OdbcMetadata.cs`, local Odbc.Metadata | largest file |
| Hosting | `ServiceInstaller` shells out to `sc.exe` (password on the command line), self-signed PFX written unprotected, `WorkerProcessHost` for the x86 worker, `Cli.cs` verbs | `Hosting/`, `Tls/` |
| CI | `ci.yml`: Windows build/test/package smoke, Linux SQL Server job with `DBPROXY_REQUIRE_SQLSERVER`; `release.yml` on `v*` tags | `.github/workflows` |
| Build setup | central package management, `TreatWarningsAsErrors=false`, xunit.v3 + MTP, no `nuget.config` | |

## 2. Principles

1. **Published packages are the contract.** Proxy `main` only references packages from a feed, never `../shared`. Local
   source override exists for development only and is never active in CI.
2. **One shared concern per Proxy PR**, each green on the existing Windows and SQL Server jobs, each independently revertable.
3. **Adapter first, delete second.** Proxy keeps its public behaviour (wire protocol, config keys, CLI verbs, exit codes)
   until a PR says otherwise. Old code is removed in the same PR only when the existing tests pass unchanged against the shared code.
4. **Security gain is the ordering signal** after dependencies: High findings first where the shared package already closes them.
5. **Never block on shared releases.** Develop Proxy PRs against the source override; the Proxy PR merges only after the
   package version it needs is published.

## 3. Phase A: Build, test and publish the shared packages (hub)

The hub has no `.github/` yet. Everything below lives in `DbDataKit/.github` and `shared/`.

### A1. Package metadata (`shared/Directory.Build.props`)
Add, for packable projects only (`IsPackable` is opt-in per csproj, today only `Odbc.Metadata` sets it; every package Proxy needs gets it):
`PackageLicenseExpression=MIT`, `PackageProjectUrl`, `RepositoryUrl`, `PackageReadmeFile` (each package already has a README),
`PackageIcon` (optional), `Copyright`, `PublishRepositoryUrl`, `EmbedUntrackedSources`, `ContinuousIntegrationBuild` when `CI=true`,
`Microsoft.SourceLink.GitHub` (`PrivateAssets=all`), `PublicApiAnalyzers` for every packable project (`PublicAPI.Shipped.txt` established at first release).
Non-packable: tests, benchmarks, `Conformance` stays packable (consumers use it for their own engine tests) but is not in Proxy's closure.

### A2. Versioning
Lockstep while 0.x: one `<Version>` in `shared/Directory.Build.props`, all packages released together from tag `kit-v0.2.0`.
Rationale: a single maintainer, tightly interdependent packages (`Connections` depends on `Secrets`, `Dialect`, `Config`), and Proxy
consuming a dozen of them: one version number removes skew. Re-evaluate at 1.0 (independent SemVer was the original plan; per-package
tags `<package>-v<semver>` are the migration path and `Directory.Build.props` already allows per-project `Version`).
Breaking change policy at 0.x: minor bump = may break; `PublicAPI.Unshipped.txt` diff is the review artifact; changelog `shared/CHANGELOG.md`.

### A3. `shared-ci.yml` (hub; on PR/push touching `shared/**`)
| Job | Runner | What |
|---|---|---|
| build-test | windows-latest | `dotnet build -c Release` (warnings are errors), `dotnet test --solution shared/DbDataKit.Shared.slnx`, `--minimum-expected-tests` guard (floor at 1,700 so a discovery failure cannot pass) |
| build-test-linux | ubuntu-latest | same; covers Unix-only paths (systemd generator, unixODBC inventory, file ACL mode bits) |
| pack | ubuntu-latest | `dotnet pack -c Release -o artifacts`, then `Microsoft.DotNet.ApiCompat`/package validation, install each package into a scratch consumer project to prove restore and a smoke call work (catches missing dependencies, e.g. the x86 worker case below) |
| live-databases | ubuntu-latest | SQL Server 2022 and Postgres 16 service containers, `DBDATAKIT_TEST_*` env vars, `*_REQUIRE_*=true` so an unreachable server fails; unskips the gated Connections, Metadata and Transfer tests. This closes the "live tests never run" gap in STATUS.md before Proxy depends on the result |
| odbc-windows | windows-latest | runs the ODBC metadata suite against the Microsoft Text driver / SQL Server ODBC where installed; Proxy relies on this package most |
| vulnerable | ubuntu-latest | `dotnet list package --vulnerable --include-transitive`, fails on High+ |
Actions pinned by SHA, `permissions: contents: read`, concurrency group per ref.

### A4. `shared-release.yml` (hub; on tag `kit-v*`, plus manual dispatch for a dry run)
1. Verify tag equals `<Version>` in props and that CI passed for the commit.
2. `dotnet pack` all packable projects, run the pack smoke test.
3. Publish `.nupkg` and `.snupkg` to **nuget.org with Trusted Publishing** (no stored API key; same mechanism Sync already uses), in a protected `nuget` environment requiring approval.
4. Create the GitHub Release with changelog excerpt and attach packages.
5. Tag name only through `env:` (never interpolated into shell; the Proxy `release.yml` issue from the review).
One-time manual steps: reserve the `DbDataKit.` package-id prefix on nuget.org; register the Trusted Publishing policy for `DbDataSync/DbDataKit` workflow `shared-release.yml` environment `nuget`.

Feed choice (decision D1): nuget.org is recommended (public, MIT, no auth in any consumer's CI). If the packages must stay private
for now, use GitHub Packages with a read PAT/`GITHUB_TOKEN` and `nuget.config` credentials; consumer CI work is the only difference.
Pre-releases (`0.2.0-preview.N`) are cut from the same workflow via tag `kit-v0.2.0-preview.1`; use them when Proxy needs an unreleased fix.

### A5. Contract job for Proxy (hub, `contract.yml`)
On `shared/**` changes: check out `DbDataProxy` at `main` (submodule pointer is not used), set `UseLocalDbDataKit=true`, build and run
Proxy's unit tests (not the SQL Server job). A failing Proxy blocks the shared PR. Sync and Build are added to this job later, outside this plan.

### A6. Bootstrap release
First tag `kit-v0.1.0` publishes the package set in section 5 (the closure Proxy needs, about 22 packages including abstractions);
everything else in `shared/` is packed in CI but marked `IsPackable=false` until a consumer needs it, to keep the public surface small.

**Exit criteria for A:** a tag builds, tests, publishes and creates a release without manual file edits; a throwaway consumer restores
`DbDataKit.Odbc.Metadata` from nuget.org and runs a call; live-database job green; `contract.yml` green against Proxy `main`.

## 4. Phase B: Proxy consumes the packages

### B1. Feed and package versions
- Add `nuget.config` to Proxy with package source mapping: `DbDataKit.*` to nuget.org (or the chosen feed), everything else unchanged.
- `Directory.Packages.props`: one property `<DbDataKitVersion>0.1.0</DbDataKitVersion>` and `PackageVersion Include="DbDataKit.X" Version="$(DbDataKitVersion)"` per package, so a bump is one line.
- Align shared pins Proxy also declares (`System.Data.Odbc`, `Apache.Arrow` family, `Microsoft.Data.SqlClient`, `System.Security.Cryptography.Pkcs` 10.0.13 pin): central package management in Proxy wins for its own references; the shared packages declare minimums only. Check `dotnet list package --include-transitive` after each adoption PR.
- Dependabot: group `DbDataKit.*` in one update PR so a release produces a single Proxy PR.

### B2. Local source override (development only)
Proxy gets `Directory.Build.local.props` (git-ignored) support:
```xml
<!-- Directory.Build.props (Proxy), last line -->
<Import Project="$(MSBuildThisFileDirectory)Directory.Build.local.props" Condition="Exists('$(MSBuildThisFileDirectory)Directory.Build.local.props')" />
```
and `build/DbDataKit.Local.targets` imported from `Directory.Build.targets`: when `UseLocalDbDataKit=true` and `$(DbDataKitRoot)` (default `..\shared`,
i.e. the hub checkout) exists, every `PackageReference Include="DbDataKit.*"` is removed and replaced with a `ProjectReference` to
`$(DbDataKitRoot)\src\<id>\<id>.csproj`. Central versions are ignored for those entries. Fails with a clear message when the
path is missing. The hub's `tools/dev.ps1 local-on` (plan 00) writes the file; until that script exists the file is two lines by hand.
CI never creates it, and a CI check fails if `UseLocalDbDataKit` is set in any committed file.
When the hub's `DbDataKit.Build` props package exists, this moves there and Proxy imports it (a later cleanup, not a prerequisite).

### B3. CI changes in Proxy
- `ci.yml`: restore from the configured feed (nothing else changes); the `--minimum-expected-tests` floors are lowered as tests move to shared (see C1) and re-raised by what Proxy adds.
- `release.yml` and `build/package.ps1` unchanged except: verify the published zip contains the new `DbDataKit.*.dll` in both the x64 service and the `worker/` x86 folder (a smoke test that loads each assembly under both bitnesses; add it to the existing package smoke step).
- New job `nupkg-closure`: `dotnet list package --include-transitive` must show no `DbDataKit.*` version other than `$(DbDataKitVersion)`.
- Warnings policy: Proxy stays `TreatWarningsAsErrors=false`; each adoption PR must not add warnings (checked by `-warnaserror` on changed projects).

### B4. Per-PR checklist (applies to every phase in section 5)
1. Develop with the local override; write the adapter and tests; run the full Proxy suite plus the SQL Server job locally via Docker.
2. Publish the needed shared version (preview if needed); switch Proxy to the published version; CI green on both jobs.
3. Update `docs/review/SECURITY.md` finding status and the Proxy README/appsettings example in the same PR.
4. Behaviour-compatibility note in the PR description: config keys, CLI verbs, wire behaviour, logs; "none" is the expected answer unless the phase says otherwise.

## 5. Phase C: Adoption order in DbDataProxy

Order is by dependency, then security value, then risk. Each step lists packages, Proxy changes, tests, and exit.

### C1. `DbDataKit.Odbc.Metadata` (replace the local project) — size S
- Packages: `DbDataKit.Odbc.Metadata`.
- Change: delete `src/DbData.Odbc.Metadata` and `tests/DbData.Odbc.Metadata.Tests` from Proxy, add the package reference to `DbDataProxy.Core`, rename namespaces (`DbData.Odbc.Metadata` to `DbDataKit.Odbc.Metadata`), adapt `OdbcMetadata.cs`.
- Adopt the hardening: construct `OdbcCatalogOptions` from `DataSourceOptions` (statement timeout from `CommandTimeoutSeconds` with a non-null default, `MaxRows`), default `OdbcConnectionPolicy` (forbids `FILEDSN`/`SAVEFILE`) with `Unrestricted` only for administrator-written config strings, `OdbcResultTruncated` surfaced as a client-visible error rather than a silent cut.
- Verify before merge: the package works in the **win-x86 worker** (`LibraryImport` against `odbc32.dll` under 32-bit; the pack smoke test and a worker integration test cover it); the 4-byte `SQLWCHAR` check does not trip on Windows; `OdbcDriverInventory` runs under both bitness registry views.
- Tests: Proxy's existing metadata tests and the Linux SQL Server job unchanged and green. Metadata unit tests move to the hub (their count is subtracted from the CI floor).
- Exit: no `DbData.Odbc.Metadata` project in Proxy; metadata calls honour timeout and row cap; ODBC primary-key reporting still passes.

### C2. `DbDataKit.Dialect.Abstractions`, `DbDataKit.Dialect` (lexer, quoting, classifier) — size M, closes S-1 partly and S-9
- Replace `SqlMarkers.Scan` with `SqlLexer`-based marker rewriting; this fixes the missing dollar-quote, backtick and `#` cases and gets the shared fuzz coverage.
- Replace the two identifier-quote implementations with `ISqlDialect.Quote/ValidateName` (rejects control characters and unterminated quotes, S-9), including the "raw name when the driver reports none" fallback made explicit.
- `ReadOnlyPolicy` / `SqlStatementClassifier` enforced in the query path: a data source with `AllowWrites=false` rejects non-`Select` statements and the deny-list calls per dialect (`OPENROWSET`, `xp_*`, DuckDB `read_csv`, `COPY`, `ATTACH`, `INSTALL`, `LOAD`). Documented as defence in depth; real enforcement comes in C5.
- **Behaviour change (flag in release notes):** statements the classifier cannot classify are refused on read-only sources. Add `Proxy:DataSources:<n>:Classifier` = `strict | warn | off` (default `strict` for new installs; `warn` for one release when upgrading, logs would-be refusals).
- Tests: hostile-SQL corpus from the Dialect package run against the Flight server's query and prepare paths; existing parameter tests unchanged.
- Exit: Proxy has one lexer and one quoting implementation; `rg "SqlMarkers"` empty.

### C3. `DbDataKit.Secrets.Abstractions`, `DbDataKit.Secrets`, `DbDataKit.Secrets.Windows` — size M, closes the plaintext-secret findings
- Resolve `secret:`/`env:`/`file:`/`dpapi:` references in config values for `ConnectionString` (whole-string and `Password=` parts), `Tls:CertificatePassword`, `Auth:Jwt` client secrets. Plain values still work but log a one-time warning naming the key (never the value) and `Proxy:RequirePlainSecretsDisabled=true` turns the warning into a startup failure.
- Wrap all log and exception output that can carry connection strings or driver text in the shared `Redactor`; register every resolved secret with it.
- Replace `Auth/PasswordHasher.cs` with the shared PBKDF2 hasher **only after** a compatibility test proves existing `PasswordHash` strings verify (golden hashes generated by the current Proxy checked in as a test fixture); `DbDataProxy hash-password` keeps its output format. If formats differ, the shared hasher reads the old format and writes the new one (rehash on next login).
- `CredentialKeyDetector` runs at startup over data source connection strings: a literal `Password=` is reported (see warning policy above).
- Tests: unit tests for each reference scheme, redaction golden tests on startup log, a "no secret appears in any log line" test driven by a data source with a sentinel password.
- Exit: example `appsettings.json` and README show only references; startup log verified secret-free.

### C4. `DbDataKit.Config.Yaml`, `DbDataKit.Config`, `DbDataKit.Config.Hosting` — size M
Decision D2: Proxy stays JSON. Adopt the strict binder and validation, not YAML (optional later: the same `ProxyOptions` read from `proxy.yaml`).
- Replace the implicit `IOptions` bind with `ConfigBinder` strict mode: unknown keys are errors with a "did you mean" suggestion, type errors name the key path, validation runs **at startup** (listen port, TLS mode vs path, data source references in `Users`, bitness rules such as "x86 only with ODBC", provider names). This ends the "typo silently ignored" behaviour.
- Add optional `schemaVersion` (absent = 1); the migration framework carries future renames (`config migrate --apply` writes `.bak`).
- Compatibility: keys are PascalCase today; the Config package defaults to camelCase, so the binder is configured case-insensitive and the example file keeps its current casing. Verify with a golden test over the shipped `appsettings.json` and `appsettings.example.json`.
- Overrides and `ShowOrigin`: `DbDataProxy config show --origin` prints effective values with their source (file, env, CLI) with secrets redacted.
- Tests: golden config corpus (valid, each invalid class), env override tests (`Proxy__Listen__Port`), the existing config tests unchanged.
- Exit: invalid config fails at startup with a precise message and exit code 2 (decision D3: keep exit code stable or document it).

### C5. `DbDataKit.Connections.*` and `DbDataKit.Metadata*` — size L, closes S-1 (DuckDB), S-2, S-10, TLS defaults
Packages: `Connections.Abstractions`, `Connections`, `.SqlServer`, `.DuckDb`, `.Odbc`, `DbDataKit.Metadata.Abstractions`, `DbDataKit.Metadata`.
Split into three PRs:

**C5a Providers.** Replace `IDbProvider`, `SqlServerProvider`, `DuckDbProvider`, `OdbcProvider`, `Providers.cs` with the shared `IDbProvider` registry. `DataSourceOptions` becomes a view over `ConnectionDefinition` (`extensions.proxy` carries bitness, pass-through, allow-writes, metadata mode, batch size). Existing keys keep working through a config migration step (schema 1 to 2).
**C5b Safety.**
- Pass-through credential injection uses the composer: strips every integrated-security style key per engine (`Trusted_Connection`, `Integrated Security`, `Authentication=*`, Kerberos keywords) and asserts none remain; the hostile-credentials test (`x};Driver={evil};UID={`) is added to the Proxy suite (S-10).
- Read-only recipes at the connection: DuckDB `access_mode=READ_ONLY`, `enable_external_access=false`, no extension autoload/install, `lock_configuration=true`; ODBC `SQL_ATTR_ACCESS_MODE` where the driver honours it; SQL Server dedicated read login documented (S-1 High: real enforcement).
- Failure model: `ConnectionFailure` replaces raw driver text in gRPC statuses; `Login` maps to `Unauthenticated` and feeds `LoginThrottle` (S-2); raw text only in local logs behind a flag and passed through the Redactor.
- TLS default for SQL Server: encrypt and verify on; existing sources that depended on `TrustServerCertificate=true` need an explicit `tls.trustServerCertificate`, with a startup warning for a transition release (**behaviour change, release note**).
- Optional pooling/limits (`ConnectionLease`): per-data-source and per-user concurrency caps and acquire timeouts (S-6); default off, enabled via config.
**C5c Metadata.** Move `ProxyFlightServer.Metadata.cs` onto `ICatalogReader` (`Metadata` package: ODBC reader, INFORMATION_SCHEMA reader, neutral records) so `GetSqlInfo`, tables, columns, keys come from one implementation; `ServerFacts` replaces the ad hoc backend-facts code. Largest diff, mechanical; golden Flight SQL metadata responses recorded from the current server and compared byte-for-byte (schemas) before and after.
- Tests: hostile DuckDB SQL (`ATTACH`, `read_csv`, `COPY TO`, `INSTALL`) returns an error on a read-only source; read login cannot write on SQL Server; throttle test through the new failure model; existing Linux SQL Server job unchanged.
- Exit: SECURITY H2 and H3 (read-only and pass-through) closed with tests, S-2 and S-10 closed; no raw driver text in any client response.

### C6. `DbDataKit.Hosting.*`, `DbDataKit.Security.Acl` (without update features) — size M, closes H4
Packages: `Hosting.Abstractions`, `Hosting`, `Hosting.WindowsService`, `Hosting.Systemd`, `Hosting.Certificates`, `Hosting.Children`, `Hosting.Cli`, `Security.Acl`.
- `ServiceInstaller` replaced by `Hosting.WindowsService` (SCM API, no `sc.exe`): service account options, password never on a command line (read from a prompt or a secret ref), failure actions kept (restart 5s/5s/60s, reset after a day), least-privilege defaults (virtual account `NT SERVICE\DbDataProxy`), data directory ACLs through `Security.Acl`.
- `CertificateProvider`: self-signed PFX generated with a protected password (DPAPI machine scope on Windows, 0600 plus secret ref elsewhere), file ACL on the PFX, rotation before expiry, `cert show/renew` through `Hosting.Cli` (H4).
- `WorkerProcessHost` moves onto `Hosting.Children` (job-object containment, startup timeout, restart policy, stdout/stderr capture, no handle inheritance); the x86 worker protocol is unchanged.
- systemd unit and Dockerfile generation through `Hosting.Systemd` and the container helper (Docker was untested; add a smoke test to Proxy CI that builds the image and hits the health endpoint).
- CLI compatibility: `DbDataProxy service install|uninstall [--name] [--account] [--password]` keeps working (`--password` accepted but warns and prefers `--password-secret`); `scripts/install.ps1` is not changed in this plan and continues to call these verbs.
- Explicitly not adopted: `/version` and drain hooks are kept as available seams (cheap) but no update client is wired.
- Tests: elevated Windows runner test installs and removes a real service (CI uses `windows-latest`, admin by default); ACL assertions on the data directory and PFX; Linux unit generation golden files; child-process kill-on-exit test.
- Exit: H4 closed; no `sc.exe` call remains in Proxy; install from `install.ps1` still produces a working service.

### C7. `DbDataKit.Dialect.Arrow` (type mapping) — size S, optional, measure first
Replace `ArrowColumnWriter`'s if-chain by the shared canonical-to-Arrow table only if a benchmark of the Proxy hot path (32,768-row batches, mixed types) shows no regression larger than 3%. Otherwise keep Proxy's writer and run the shared golden table as a consistency test against it (Decimal128 overflow guard, timestamp UTC rule, unsigned handling). Decision D4 after the benchmark.

### Summary table

| Step | Packages | Proxy area | Closes | Size | Depends on |
|---|---|---|---|---|---|
| C1 | Odbc.Metadata | Core metadata, tests | hardening of the first shared package | S | A, B |
| C2 | Dialect.Abstractions, Dialect | query path, quoting | S-9, part of S-1 | M | C1 |
| C3 | Secrets.Abstractions, Secrets, Secrets.Windows | config, auth, logs | plaintext secrets | M | B |
| C4 | Config.Yaml, Config, Config.Hosting | startup | silent bad config | M | C3 (secret refs in config) |
| C5a-c | Connections.*, Metadata* | providers, flight, metadata | S-1 (DuckDB), S-2, S-10, TLS, S-6 | L | C2, C3, C4 |
| C6 | Hosting.*, Security.Acl | service, TLS file, worker | H4 | M | C3 |
| C7 | Dialect.Arrow | Arrow writer | none (consolidation) | S | benchmark |

C3, C4 and C6 can be developed in parallel branches (different files); C5 follows C2-C4.

## 6. Deliberately skipped

| Package | Reason |
|---|---|
| `Update`, `Update.Abstractions`, `Update.Service`, `Update.Detect`, `Update.Tools`, `dbdata-updater` | complexity and key custody not settled; Proxy keeps `install.ps1` and re-run-to-upgrade. Revisit after C6; the layout and drain/`/version` seams are already in place |
| `Transfer.*` | Proxy streams rows to Arrow and never stages or loads |
| `Connections.Libraries`, `.Descriptor`, `.Postgres`, `.Odbc` extras beyond need | no plugin or dynamic-library use in Proxy; Postgres is not a Proxy provider (add `.Postgres` only if a provider is requested) |
| `Config.Testing`, `Secrets.Cli`, `Dialect.Conformance` | optional test/CLI aids; consider `Config.Testing` in C4 tests |

## 7. Risks and mitigations

| Risk | Mitigation |
|---|---|
| x86 worker cannot load a shared package (target, native dependency) | pack smoke test under win-x86 in A3; C1 worker integration test before any other package |
| Shared churn at 0.x breaks Proxy | lockstep versions, exact pins, contract job on every shared PR, Dependabot group PR |
| Behaviour changes surprise operators (TLS verify, classifier refusals, plain secrets warnings) | each flagged in release notes; one release in `warn` mode where possible; config migration tool with `.bak` |
| Proxy performance regression on the Arrow path | C7 gate; C5 benchmarks of query latency on a fixed table; no shared code on the per-row path except the writer decision |
| Windows service tests flaky on hosted runners | run them in a dedicated job, retry once, upload service logs as artifacts |
| Release friction (maintainer is one person) | single lockstep tag, one approval gate, Dependabot does the Proxy bumps |
| Live-database gaps (50 skipped tests) | A3 `live-databases` and `odbc-windows` jobs run before kit-v0.1.0 |

## 8. Decisions needed

| # | Decision | Recommendation |
|---|---|---|
| D1 | Feed: nuget.org vs GitHub Packages | nuget.org with Trusted Publishing |
| D2 | Proxy config stays JSON, YAML later | JSON now |
| D3 | Exit code and message format for invalid config | stable code 2, documented |
| D4 | Arrow writer replacement | decide after benchmark |
| D5 | Lockstep versions until 1.0 | yes |
| D6 | Classifier default for upgraded installs | `warn` for one release, then `strict` |
| D7 | TLS verify default for SQL Server sources | verify on, explicit opt-out, one-release warning |

## 9. Acceptance

- `kit-v0.1.0` published from the hub by tag, with live-database, pack-smoke and vulnerability jobs green.
- Proxy builds from published packages only, has no `DbData.*` project of its own, and its CI is unchanged in shape.
- Findings H2, H3, H4, S-1, S-2, S-9, S-10 and the plaintext-secret finding have regression tests in Proxy or in the shared suite that Proxy exercises.
- `appsettings.json` contains no plaintext secrets in the shipped example; invalid config fails at startup with a named key.
- Upgrade from the last pre-adoption Proxy release to the adopted one works with an unchanged `appsettings.json` (plus warnings), verified by an upgrade test in CI.
