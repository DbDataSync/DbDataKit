# DbDataKit.Secrets

> **Where secrets live.** `secret:<name>` references are answered by the encrypted store in [`DbDataKit.Secrets.Store`](../DbDataKit.Secrets.Store/README.md) (a file, `secrets.yaml`, whose values are encrypted with a key kept
> separately: DPAPI on Windows, a permission-restricted file elsewhere, or a key the platform provides). Back up that key apart from the file; either alone is useless. To move a server use `secret export` and `secret import` (commands in
> [`DbDataKit.Secrets.Cli`](../DbDataKit.Secrets.Cli/README.md)), never a copy of the key. The providers below answer what the store does not have.

Runtime for Plan 02. Depends on `DbDataKit.Secrets.Abstractions`.

## Public contract
- `SecretResolverChain(providers, audit?, redactor?)`: first provider that `CanHandle` and returns non-null wins;
  resolved values are registered with the redactor; every attempt is audited by reference name only.
- `EnvSecretProvider`: `env:NAME`; `secret:<name>` maps to `DBDATA_SECRET_<NAME>` (non-alphanumerics become `_`);
  prefix or mapping configurable, `secretPrefix: null` disables `secret:` handling.
- `FileSecretProvider`: `file:<path>`, contents trimmed, `MaxBytes` (default 64 KiB; oversize throws, missing is null).
  Optional `SecretsDirectory` makes `secret:<name>` resolve to `<dir>/<name with ':' -> '_'>` (systemd
  `$CREDENTIALS_DIRECTORY`, Docker `/run/secrets`); traversal is refused.
- The encrypted store that answers `secret:<name>` first is in `DbDataKit.Secrets.Store` (`EncryptedFileSecretStore`); the `secret` commands are in `DbDataKit.Secrets.Cli`.
  (`LegacyEnvProvider`, which mapped Build's `DBDATABUILD_*` and Sync's `DBDATASYNC_SECRET_*` variables, is gone: plan 13, a clean break.)
- `Redactor`: exact value + URL-encoded (both `EscapeDataString` and `UrlEncode`), base64 (UTF-8), JSON-escaped variants,
  longest first; always scrubs `Password=...`-style fragments and URL user-info. Values shorter than `MinLength` (3) are ignored.
- `DelegateSecretAudit`, `PasswordHasher` (`PBKDF2-SHA256$iter$salt$hash`, fixed-time compare, `NeedsRehash`).

## Decisions
- Registered values live as strings inside the redactor; that is inherent to text replacement.
- Fragment scrubbing stops at `;`, newline or the closing quote, so it can over-redact prose in the same line (safe side).
- The Sync env-var sanitisation (`DBDATASYNC_SECRET_<REF>`) is an assumption: confirm against `ClrKernel.Core.Secrets` in Phase 0
  or pass a custom `EnvMapping`.
- Plan item "statement-log parameter hashing" and the deprecation-window policy flag are not part of this package.
