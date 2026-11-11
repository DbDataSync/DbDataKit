# DbDataKit.Secrets.Abstractions

Contracts and pure logic for Plan 02. No dependencies.

## Public contract
- `SecretRef.Parse/TryParse`: `secret:<name>`, `env:NAME`, `file:<path>`, `dpapi:<base64>` (there is no `keyring:`: no OS keychain, which is per user and does not suit services).
  `ToAuditString()` elides DPAPI blobs.
- `SecretName`: `dbdatakit:<service>:<kind>:<name>[:<part>]`, lower-cased, segments `[A-Za-z0-9._-]`.
  Read/write split is the `read`/`write` part (no fallback between them). `<service>` is the service or module that owns the secret and follows the environment
  variables (`DBDATAKIT_`): `server` (the DbDataKit console), `gateway`, `runner`, `sync`, `build`, `store`, `workspace`; for example
  `dbdatakit:build:connection:warehouse:write`, `dbdatakit:server:state:<name>`, `dbdatakit:gateway:identity:signing-keys`.
  **Clean break (plan 33 SC31c):** the names were `dbdatakit:<service>:...` (`dbdatakit:server:...` for the console, `dbdatakit:gateway:...` before the rename to Gateway, `dbdatakit:server:identity:signing-key`
  for the console's identity, which is now `dbdatakit:server:identity:signing-key`). A name that does not start `dbdatakit:` is not a secret name, there is no alias, and a store written under the old names
  does not find its secrets: they are set again under the new names (the table is in `docs/plans/RELEASE-NOTES-PENDING.md`). The environment variable of a secret follows its name
  (`DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`), and so does the file of a credentials directory (`dbdatakit_sync_connection_orders`).
  `scripts/check_secret_names.py` fails on any tracked file that names a secret `dbdatakit:...`.
- `SecretValue`: `ToString()` is `***`; `Reveal()` returns a span over the internal buffer; zeroed on `Dispose`;
  `Reveal()` after dispose throws.
- `ISecretResolver`, `ISecretProvider` (chain link: `CanHandle`, `TryResolveAsync` returning null for "not here"),
  `ISecretStore` (names only), `IRedactor`, `ISecretAudit` (names, never values), `SecretNotFoundException`.
- `CredentialKeyDetector`: `FindInConnectionString`, `FindInProperties`, `ContainsCredential`,
  `IsCredentialKey` (strict, whole key) and `IsCredentialPropertyKey` (broader: `DbPassword`, `GatewayToken`).
  Findings carry the key, never the value.

## Decisions
- Values that are themselves secret refs (`Password=env:X`) are not plaintext (`allowReferences`, default true).
- Property-bag keys ending in `ref` (`PasswordRef`) are never flagged.
- `Authentication=` with a value containing "password" is reported as `PasswordAuthenticationMode`
  (informational: the password may legitimately come from the store at connect time; the caller decides severity).
- `scheme://user:pass@host` is reported as `UrlUserInfo`.
- Superset of Sync's detector (`password|pwd|passwd|secret|accountkey|apikey`) plus token/sas/pat etc.
