# Plan 03: Windows Services and Hosting (`DbData.Hosting`)

Status: proposed. Depends on: Plan 02 (ACL helper, secret handling for service accounts). Feeds Plan 08 (updater needs service control).

## 1. Problem

| | Sync | Proxy | Build |
|---|---|---|---|
| Service | `Cli/ServiceCommand.cs` (Windows), `SystemdService.cs`; `Hosting.WindowsServices` | `Hosting/ServiceInstaller.cs` shells out to `sc.exe` (ArgumentList), auto-start, restart-on-failure | none (CLI; web/MCP are local, on demand) |
| Account | not reviewed in detail | **LocalSystem** by default; `--password` visible on the sc.exe command line during install | n/a |
| Container | root, SDK image | root, untested | n/a |
| Certificates | `DbDataSync.Certificates` (store, ADCS enrollment, managed self-signed) | `CertificateProvider` self-signed; PFX with empty password, default ACLs | localhost token hardening only |
| Child processes | `ProcessSupervisor` spawns TaskRunner per run; loopback state server + token in env | `WorkerProcessHost` spawns 32-bit worker on loopback TCP; secret in env | n/a |
| Directories/ACLs | repo root and data dir by env | `%ProgramData%\DbDataProxy` inherited ACLs; Program Files install | n/a |
| Install | `dotnet tool` + `service install` | `scripts/install.ps1` | Scoop / archive |

Related review findings: Proxy S-3 (key ACLs), S-4 (LocalSystem/root), S-12 (worker loopback port race);
Sync 9.6 (network-trust auth behind proxy), 9.9 (bootstrap invite file), 9.10 (root container).

## 2. Goals
1. Safe-by-default install: least-privileged identity, locked-down directories, no secrets on command lines.
2. One implementation of install/uninstall/start/stop/status/recovery for Windows (SCM) and systemd.
3. One certificate story for any Kestrel host.
4. One supervised-child-process pattern with no TCP-port race.
5. Hosting conventions (health, version, drain, logging) shared so the updater (Plan 08) works the same everywhere.

## 3. Design

### 3.1 Service control (`DbData.Hosting.WindowsService`)
- Use the Service Control Manager API directly (P/Invoke `CreateServiceW`, `ChangeServiceConfig2W`,
  `StartService`, `ControlService`, `QueryServiceStatusEx`) instead of `sc.exe`, so passwords never appear on
  a command line and error codes are typed. Fall back to `ServiceController` for status.
- **Default identity: virtual account `NT SERVICE\<Name>`**; supported alternatives: gMSA (`DOMAIN\name$`, no
  password) and an explicit account with the password read from stdin or a `secret:` ref. LocalSystem requires
  `--allow-localsystem`.
- Settings: auto-start (delayed), description, dependencies, failure actions (restart 3x, reset period),
  service SID type `unrestricted`/`restricted`, required privileges trimmed (`sc privs` equivalent),
  Event Log source registration, binary path quoting and `current` junction (see Plan 08).
- Commands (shared via `DbData.Hosting.Cli`): `service install|uninstall|start|stop|restart|status|run` with
  `--account`, `--password-stdin`, `--data-dir`, `--console` for foreground. Elevation detection with a clear
  message (optionally UAC relaunch).
- Idempotent install: reconfigure if present; refuse to downgrade silently.

### 3.2 Directories and ACLs (`AclHelper`, shared with Plan 02)
Layout:
```
%ProgramFiles%\<Product>\versions\<ver>\...   binaries; Administrators+SYSTEM write; service account read/execute
%ProgramFiles%\<Product>\current              junction to the active version (Plan 08)
%ProgramData%\<Product>\config                service account read; Administrators write
%ProgramData%\<Product>\data|secrets|certs    service account RW; Administrators full; Users none; inheritance cut
%ProgramData%\<Product>\logs                  service account write; Administrators read
%ProgramData%\<Product>\updates               service account write (staging), updater read
```
Installer creates and verifies the ACLs; `service doctor` re-verifies and reports drift (world-readable key
files, writable binaries). Linux equivalents: `/opt/<product>`, `/var/lib/<product>`, `/etc/<product>`, owner
`<product>` user, 0750/0600.

### 3.3 systemd and containers
- Generate unit files (extending Sync `SystemdService.cs`): `DynamicUser=yes` or dedicated user,
  `LoadCredential=` for secrets (Plan 02), `ProtectSystem=strict`, `ProtectHome`, `NoNewPrivileges`,
  `PrivateTmp`, `ReadWritePaths=` for data, `Restart=on-failure`, `WatchdogSec` when health is wired.
- Dockerfile template: non-root user, `HEALTHCHECK` against the health endpoint, pinned base digests,
  read-only root filesystem where possible, no SDK in runtime images (Sync's default image includes the SDK
  for in-app library installs; treat that as an explicit "dev image" and make `runtime` the default).

### 3.4 Certificates (`DbData.Hosting.Certificates`)
Merge Sync's `DbDataSync.Certificates` (Windows store lookup, ADCS enrollment, managed self-signed) and
Proxy's `CertificateProvider` (self-signed with SAN machine/FQDN/loopback, 2048-bit RSA):
- Modes: `File` (PFX/PEM, password via secret ref), `Store` (thumbprint/subject in LocalMachine\My, ACL on
  private key for the service identity), `Enroll` (ADCS), `SelfSigned` (managed, auto-renew before expiry).
- Self-signed PFX gets a random password stored through Plan 02, restrictive ACL, and is exportable only if
  asked; export of the public `.cer` for client trust is a separate file.
- Refuse plain HTTP/`TlsMode.None` on non-loopback binds unless explicitly overridden (Proxy S-5, Sync 9.6).
- Expiry/rotation surfaced on the health endpoint and `service doctor`.

### 3.5 Child process supervision (`DbData.Hosting.Children`)
Common `ChildProcessHost` for Sync's TaskRunner and Proxy's x86 worker:
- Windows Job Object with `KILL_ON_JOB_CLOSE` and parent-death detection so children never outlive the host.
- Handoff of tokens through environment or an inherited handle, never argv.
- **Transport without a port race**: named pipe (Windows) / Unix domain socket with ACL/permissions instead of
  allocate-then-release TCP (Proxy S-12). gRPC over named pipes/UDS is supported by .NET; Sync's loopback
  state server can use the same endpoint type. TCP loopback remains available with a random per-launch secret.
- Restart policy, backoff, structured exit reporting, resource limits (memory/CPU via job object).

### 3.6 Host conventions (`UseDbDataHost`)
Builder extension standardising: service/systemd integration, `--console`, graceful shutdown with a **drain**
hook (Sync `UpdateDrainMiddleware` generalised: stop accepting new work, wait up to N seconds), `/health`
(liveness + readiness with component checks) and `/version` (product, version, commit, schema versions;
required by the updater's post-update check), structured logging to Event Log/file/journal with the Plan 02
redactor installed, request limits defaults (Proxy S-6: concurrency, timeouts, message size), `ForwardedHeaders`
handling documented once.

## 4. Package layout
```
DbData.Hosting               host builder, drain, health/version, limits
DbData.Hosting.WindowsService SCM wrapper, install/uninstall, event log, ACL checks
DbData.Hosting.Systemd       unit generation
DbData.Hosting.Certificates  cert modes, self-signed, store, ADCS
DbData.Hosting.Children      child process host + pipe/UDS transport
DbData.Hosting.Cli           shared `service` and `cert` command set
```

## 5. Phases

| Phase | Work | Exit |
|---|---|---|
| 1 | `AclHelper` + directory layout spec; ACL tests on a Windows runner | Tests green on windows-latest |
| 2 | SCM wrapper replacing `sc.exe`; virtual account default; password via stdin/secret. Proxy adopts first (smallest, highest severity: S-3/S-4) | Fresh install runs as `NT SERVICE\DbDataProxy`; service doctor clean |
| 3 | Certificates merge (start from Sync's project); Proxy adopts random PFX password and ACLs | No empty-password PFX |
| 4 | Host conventions: health/version/drain/limits; Sync adopts drain/health | Updater (Plan 08) can poll `/version` |
| 5 | Child host with named pipe/UDS + job object; Proxy worker and Sync TaskRunner adopt | Port race removed; children die with parent |
| 6 | systemd generator and hardened Dockerfile templates | Non-root containers, unit hardening score reviewed |
| 7 | Build adopts only what applies (cert/ACL helpers for its web UI if ever remote); optional | n/a |

## 6. Risks
- Virtual accounts can't access network shares or domain resources; document gMSA for those cases (SQL
  Server integrated auth through pass-through or Windows auth).
- DuckDB data paths, ODBC DSNs (system vs user DSN) and 32-bit drivers behave differently under a virtual
  account; test the Proxy worker under it (user DSNs are invisible to a service identity).
- CI needs an elevated Windows runner (windows-latest runs as admin; confirm service install tests are safe
  and cleaned up).
- P/Invoke surface must be small and well tested; keep a `sc.exe` fallback only for diagnostics.

## 7. Acceptance
No product installs as LocalSystem or root by default; no secret on a command line; `service doctor` passes
in CI; private keys and secret files are not readable by `Users`; children cannot outlive the host; every
service exposes `/version` and drains on stop.
