# Plan 02: Secrets (`DbDataKit.Secrets`)

Status: proposed. Depends on: none. Consumed by Plans 01 (secret-typed keys), 03 (service contexts), 06 (connections).

## 1. Problem

| | Sync | Proxy | Build |
|---|---|---|---|
| Store | `ClrKernel.Core.Secrets` 0.11.0: OS keyring, env fallback `DBDATASYNC_SECRET_<REF>` | **none**; passwords in `appsettings.json` connection strings, JWT key and cert password plaintext | env vars only: `DBDATA_BUILD_<CONN>_<READ\|WRITE>` hold whole connection strings |
| Reference in config | `CredentialSecretRef` (`dbdatasync:connection:<name>`) | none | none (name derived from connection name) |
| Redaction | `ConnectionDiagnostics.Redact` (exact literal only) | none; raw DB error text returned to clients; `LogSql` default true | `DriverFailure` never echoes driver text; statement log keeps parameters |
| Credential-in-config checks | `ConfigValidation.cs:55-120` on `ConnectionString` only (not `Properties`) | none | n/a |
| Gaps | `Properties` can hold passwords; keyring is per-user, awkward for services | S-3 High: plaintext secrets, unprotected PFX, install-time `--password` on command line | `token_command` runs a program from env value; statement log |

Review notes: the internals of `ClrKernel.Core.Secrets` (fallback encryption, memory handling) were not reviewed
and must be before it becomes the foundation (Phase 0).

## 2. Goals

1. One reference syntax usable in any definition file; **config never contains secret values**.
2. Resolution works in interactive, service, container, and CI contexts.
3. Secrets stay in memory as short-lived, redacting values; one redactor used on all error/log paths.
4. Compatible with existing products' conventions during migration.

## 3. Design

### 3.1 Reference syntax
`SecretRef` parsed from a string in config:

| Form | Meaning |
|---|---|
| `secret:<name>` | resolve through the configured provider chain |
| `env:NAME` | environment variable |
| `file:/path` or `file:C:\path` | file contents (trimmed); supports Docker/K8s secrets and systemd `LoadCredential` |
| `keyring:<service>/<name>` | OS keyring (Credential Manager, Keychain, Secret Service) |
| `dpapi:<base64>` | inline DPAPI-protected blob (LocalMachine or CurrentUser scope), Windows only |
| `azkv:`, `vault:`, `awssm:` | later providers behind the same interface |

Naming convention generalises Sync's: `dbdata:<product>:<kind>:<name>[:<part>]`, e.g.
`dbdata:build:connection:warehouse:write`, `dbdata:sync:connection:erp`, `dbdata:proxy:datasource:erp`.
Read/write split (Build's "no fallback from read to write") is encoded as the `:read`/`:write` part.

### 3.2 API
```csharp
interface ISecretResolver { ValueTask<SecretValue> ResolveAsync(SecretRef r, CancellationToken ct); }
interface ISecretStore    { Task SetAsync(SecretName n, SecretValue v); Task DeleteAsync(SecretName n);
                            IAsyncEnumerable<SecretName> ListAsync(string prefix); }   // names only
sealed class SecretValue : IDisposable { /* ToString() == "***"; Reveal(): ReadOnlySpan<char>; zero on Dispose */ }
interface IRedactor { string Redact(string text); void Register(SecretValue v); }
```
Provider chain order is configuration (`secrets.providers: [env, keyring, file]`) with a safe default per
deployment mode (below). `ISecretResolver` never logs values; resolve events are audit-logged by name.

### 3.3 Deployment contexts (the hard part)
- **Interactive CLI (Build, Sync dev)**: keyring then env. Build keeps the whole-connection-string env var as
  a compatibility provider (`DBDATA_BUILD_<CONN>_<READ|WRITE>`, `LegacyEnvProvider`) so no user breaks.
- **Windows service**: Credential Manager is per user profile, awkward for virtual accounts and gMSA. Provide
  `DpapiMachineStore`: secrets in `%ProgramData%\<Product>\secrets\` encrypted with DPAPI LocalMachine
  scope plus per-product entropy, ACL restricted to the service account and Administrators (ACL helper lives
  in Plan 03). Written by `<tool> secret set` run elevated. Document the trade-off: any process in the machine
  scope with the entropy can decrypt, so ACLs are the control.
- **Linux service/systemd**: `file:` references to `$CREDENTIALS_DIRECTORY` (systemd `LoadCredential`) or
  root-owned 0600 files; keyring optional.
- **Containers**: `file:` (Docker/K8s secrets) and `env:`; never baked into images.
- **CI**: `env:` only.

### 3.4 Redaction and leak prevention
- One `IRedactor` scrubbing exact values plus common encodings (URL-encoded, base64, JSON-escaped) and
  connection-string `password=`-style fragments. Replaces Sync's `Redact` and is applied by default to
  exception messages, logs, statement logs and API responses.
- Adopt Build's rule: do not echo driver/DB error text at all by default; emit type + error code
  (`DriverFailure`). Proxy gets it via Plan 06's `ConnectionFailure`.
- `CredentialKeyDetector` (from Sync `ConfigValidation.cs:55-120`: `password|pwd|passwd|secret|accountkey|apikey`
  plus token/sas/pat/`Authentication=`) used by config validation (Plan 01 `[Secret]` and "no plaintext" rule)
  over `ConnectionString` **and** `Properties` (closes Sync finding 9.4) and over Proxy's connection strings.
- Statement log (Build F-6): parameter values logged as type + hash by default; opt-in values.
- Memory: `SecretValue` zeroes on dispose; pass-through credentials in Proxy are held as `SecretValue` for
  session lifetime only.

### 3.5 Files and key material
`SecretFile`/`AclHelper` ensure 0600 / restricted ACL on PFX files, key files and secret blobs. Self-signed
PFX gets a random password stored via the store, not null (closes Proxy S-3). JWT signing keys referenced
as `secret:` rather than plaintext.

### 3.6 CLI
Shared command set mounted by each product (`dbdata secret ...` via `DbDataKit.Secrets.Cli`, or
`<product> secret ...`): `set <name>` (prompt or stdin, never argv), `get <name> --reveal`, `list`, `rm`,
`test <ref>` (resolves and reports ok/not found without printing), `import-env`. Service passwords are
never passed on a command line (Plan 03 reads from stdin/secret).

### 3.7 Policy
`secrets.allowPlaintextInConfig: false` default. Detector hits are errors; a deprecation window allows warnings
(Proxy) before enforcement. `token_command` (Build F-1) moves behind a policy flag `allowExternalCommands`
(off in MCP/web contexts) and is documented as trusted-environment only.

## 4. Package layout
```
DbDataKit.Secrets.Abstractions   SecretRef, SecretValue, ISecretResolver/Store, IRedactor, detector
DbDataKit.Secrets                chain, env/file/legacy-env providers, redactor
DbDataKit.Secrets.Keyring        OS keyring provider (wraps ClrKernel.Core.Secrets or replaces it)
DbDataKit.Secrets.Windows        DPAPI machine/user providers, ACL helper
DbDataKit.Secrets.Cli            shared command set
```

## 5. Phases

| Phase | Work | Exit |
|---|---|---|
| 0 | Review `ClrKernel.Core.Secrets` (threat-model fallback encryption, memory); decide wrap vs fold into this repo. Write the threat model. | Decision recorded |
| 1 | Abstractions + env/file providers + redactor + detector; unit and fuzz tests for redaction | Package published |
| 2 | Sync adopts: map `dbdatasync:connection:*` refs and `DBDATASYNC_SECRET_*` env through compat; extend detector to `Properties`; Viewer redaction | No config changes needed for existing repos |
| 3 | Build adopts: `LegacyEnvProvider` keeps `DBDATA_BUILD_*`; add `secret:` references as an option; statement-log redaction | Existing envs still work |
| 4 | DPAPI machine store + ACL helper (needs Plan 03 service identity decisions) | Service reads secrets as virtual account in a Windows CI test |
| 5 | Proxy adopts: `PasswordSecret`/`ConnectionStringSecret`, JWT key and cert password via refs; startup warning for plaintext, then error; random PFX password | S-3 closed; install.ps1 sets ACLs |
| 6 | Cloud providers (Key Vault etc.) if needed | n/a |

## 6. Risks
- Per-user keyring vs service accounts is the main operational trap; Phase 4 and docs must cover it explicitly.
- Compatibility: three existing conventions must keep working through at least one release each.
- Machine-scope DPAPI is not a boundary between processes on the same host; say so.
- Whole-connection-string secrets (Build) vs password-only secrets (Sync): support both. The connection
  composer (Plan 06) accepts either `ConnectionStringSecret` or `PasswordSecret`.

## 7. Acceptance
No plaintext credentials in any product's shipped config or examples; one redactor on every log/error path;
`secret test` works in service, container and CLI contexts; detector enforced over `Properties`; shared threat
model doc exists.
