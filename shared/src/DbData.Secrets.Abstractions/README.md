# DbData.Secrets.Abstractions

Contracts and pure logic for Plan 02. No dependencies.

## Public contract
- `SecretRef.Parse/TryParse`: `secret:<name>`, `env:NAME`, `file:<path>`, `keyring:<service>/<name>`, `dpapi:<base64>`.
  `ToAuditString()` elides DPAPI blobs.
- `SecretName`: `dbdata:<product>:<kind>:<name>[:<part>]`, lower-cased, segments `[A-Za-z0-9._-]`.
  Read/write split is the `read`/`write` part (no fallback between them).
- `SecretValue`: `ToString()` is `***`; `Reveal()` returns a span over the internal buffer; zeroed on `Dispose`;
  `Reveal()` after dispose throws.
- `ISecretResolver`, `ISecretProvider` (chain link: `CanHandle`, `TryResolveAsync` returning null for "not here"),
  `ISecretStore` (names only), `IRedactor`, `ISecretAudit` (names, never values), `SecretNotFoundException`.
- `CredentialKeyDetector`: `FindInConnectionString`, `FindInProperties`, `ContainsCredential`,
  `IsCredentialKey` (strict, whole key) and `IsCredentialPropertyKey` (broader: `DbPassword`, `ProxyToken`).
  Findings carry the key, never the value.

## Decisions
- Values that are themselves secret refs (`Password=env:X`) are not plaintext (`allowReferences`, default true).
- Property-bag keys ending in `ref` (`PasswordRef`) are never flagged.
- `Authentication=` with a value containing "password" is reported as `PasswordAuthenticationMode`
  (informational: the password may legitimately come from the store at connect time; the caller decides severity).
- `scheme://user:pass@host` is reported as `UrlUserInfo`.
- Superset of Sync's detector (`password|pwd|passwd|secret|accountkey|apikey`) plus token/sas/pat etc.
