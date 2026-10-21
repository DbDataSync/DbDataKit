# Security Findings (consolidated)

Static review only; nothing was built or run, and dependency vulnerability scans were not performed.
Severities are the reviewers'. Evidence and remediation per finding are in `<Product>/docs/review.md`. Two High
findings were spot-checked: `DbDataSync/src/DbDataSync.Libraries/LibraryInstaller.cs:199` interpolates
`p.Id`/`p.Version` directly into a generated `<PackageReference>`; `DbDataProxy` gates `AllowWrites` in
`ProxyFlightServer.cs:179`.

## High

| # | Repo | Finding | Remediation |
|---|---|---|---|
| H1 | Sync | Unvalidated `PackageId`/`Version` in library install: MSBuild injection (csproj generation) and path traversal (`Directory.Delete`) | Validate id against NuGet id regex and version against SemVer; resolve paths and assert they stay under the library root; XML-escape or use the NuGet API instead of string templates |
| H2 | Proxy | Query path is unrestricted; `AllowWrites` only gates update commands, so "read-only" isn't enforced. DuckDB sources give authenticated users host file read/write | Enforce read-only at the connection (read-only login / `access_mode=READ_ONLY`, `enable_external_access=false` for DuckDB as Build does) and add a parser-based statement guard |
| H3 | Proxy | Pass-through auth accepts any credentials, unthrottled; proxy relays password guessing to the DB | Per-client/IP lockout and rate limit, audit log of failures, option to require proxy-level auth |
| H4 | Proxy | Self-signed TLS private key written with an empty password and default ACLs | Restrict ACL to the service account, protect with DPAPI/keyring, prefer supplied certs |

## Medium

- **Proxy:** service installs as LocalSystem; Docker image runs as root; no limits on concurrency, rows or
  bytes; command timeout defaults to none; raw DB error text returned to clients; installer/CI weaknesses
  (see detail doc). Backend credentials stored plaintext in connection strings with no startup validation.
- **Sync:** unvalidated driver ids and file names in paths (`DriversController`, `FilesController`);
  `ConnectionConfig.Properties` bypasses the credential-in-config check and is readable by Viewers;
  SQL Server defaults to `TrustServerCertificate=true`; network-trust auth is unsafe behind a same-host
  reverse proxy; Admin can execute arbitrary code by design (accepted, document it).
- **Build:** `token_command` runs an arbitrary program taken from the Flight SQL login value; native-lib
  checksum comes from the same release as the lib; GitHub Actions use mutable tags and the release job
  pushes to `main`; `ReadGuard` is regex-based and mishandles Postgres `E''` strings; hook risk labels are
  author-declared and hook scripts run on the write login.

## Low

- Sync: session ids stored unhashed; `ci.yml` has no `permissions:` block; Actions not SHA-pinned; container root.
- Build: plan hash unkeyed; statement log retains parameter values; no central package versions, no
  vulnerability scan, mixed test package versions.
- Proxy: credential injection uses connection-string builders (smuggling via user/password looks blocked)
  but `Authentication=`-style keys aren't stripped.
- All: no dependency vulnerability scanning in CI.

## Checked and found sound

Sync: auth closed by default, typed YAML deserialization, `ArgumentList` for processes, identifier escaping.
Build: strict YAML reader, identifier quoting, DuckDB sandboxing, hook path-traversal checks, localhost
web/MCP hardening, no read→write credential fallback, driver-message scrubbing.
Proxy: package versions current. No hardcoded production credentials in any repo; only throwaway test-container
passwords (Sync test SQL Server, Build `scripts/test-engines.sh`).

## Cross-cutting recommendations

1. One connection-string builder with an attribute allow-list (ODBC `DRIVER=`, `DSN=`, `FILEDSN=`, `SAVEFILE=`,
   `Authentication=`) used by all three.
2. One secrets resolver; no plaintext credentials in config; shared redactor on all error/log paths (Proxy first).
3. One read-only guard based on a SQL parser, tested against a shared corpus including dialect quirks (`E''`).
4. Shared path-safety helper (resolve + assert-under-root) for driver ids, file names and library paths.
5. CI baseline: `permissions:` least privilege, SHA-pinned Actions, `dotnet list package --vulnerable` and
   `npm audit`, Dependabot, no direct pushes to `main` from release jobs, checksums published separately.
6. Non-root containers and a non-LocalSystem service account.
7. Resource limits (rows, bytes, concurrency, timeouts) as shared defaults for anything that executes client SQL.
8. Follow-up: run the dependency scans and a build/test pass; none was done in this review.
