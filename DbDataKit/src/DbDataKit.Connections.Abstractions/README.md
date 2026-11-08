# DbDataKit.Connections.Abstractions

Contracts (plan 06) for the shared connection definition, provider interfaces, credential policy and the failure model. No client libraries.

## Contract

- `ConnectionDefinition` (schema `connection/1`, and `connection/2` which adds the optional sections `cache` and `checks`, plan 24a UC03): `engine`, `dialect`, `access` (`readonly|readwrite`), `address` XOR `connection_string` XOR `connection_string_secret`, `auth`, `read_login`, `write_login`, `tls`, `timeouts`, `properties`, `extensions`.
  Keys are snake_case; `schema:` is required. Load with `ConnectionDefinitionLoader.Load(yaml)`: strict (unknown keys are errors with a suggestion), then semantic rules: address XOR string, no write login on a read-only connection, no plaintext credentials in a connection string, no credential/host/TLS keys in `properties`, valid secret references, complete logins. `ConnectionDefinitionLoader.JsonSchema()` generates the JSON Schema.
- `LoginRole` and `ConnectionDefinition.LoginFor`: **read never falls back to the write login**; write may fall back to `auth`.
- TLS defaults: encrypt on, verify on, `trust_server_certificate` off. Opting out is explicit, per connection, and reported as a warning by the loader; `ConnectionPolicy.AllowTlsOptOut=false` refuses it outright.
- Timeouts are never null (`connect_s` 15, `command_s` 300).
- `IDbProvider`, `IConnectionStringComposer`, `ConnectionContext`, `ResolvedLogin`, `ReadOnlySession` (recipe: connection-string keys, session statements, honest `ReadOnlyEnforcement` level), `ConnectionTestResult`.
- `ConnectionFailure { Kind, Code, Engine }`: `PublicMessage` is fixed text per kind, never driver text. `FailureClassifier` maps any exception through pluggable `IFailureMapper`s (engine packages), then SQLSTATE, then exception types. Raw text exists only when the classifier is built with `includeRaw: true`, and is passed through the Plan 02 redactor. `ConnectionFailureException` does not keep the inner exception.
- `CredentialPolicy`: the deny-list for `properties` (shares `CredentialKeyDetector` with Plan 02) and the integrated-security key list used by pass-through injection.

## Decisions

- Product settings live under `extensions.<product>` and are kept as YAML nodes (`ExtensionBag`), so each product binds its own part without the shared type knowing it.
- Plan 04's `ICatalogReader` does not exist yet, so `IDbProvider` has no `Catalog(...)` member; it is added when Plan 04 part B lands.
- `schema: connection/1` is checked by the loader before binding (Config's `schema: kind/int` form is not implemented), and bound with `VersionKey = "schema"`.

**Format 2, the serving data source (plan 24a UC03).** `schema: connection/2` is `connection/1` plus two optional sections, so a data source can carry its cache setting with the definition that is published and promoted. `cache:` has `enabled` (off unless `true`), `ttl` (`90s`, `5m`, `2h`; at most `24h`; default `5m`), `scope` (`caller`, `rules`, `shared`; default `caller`) and `dataset`; the values are checked with `CacheSection` of `DbDataKit.Config.Yaml`, and the cache cannot be turned on for a data source that names no server and no database (no `address` host or database and no `connection_string`: it has no lineage identity to expire entries by, `DBD-CONN-010`). `checks:` is the place of the data source's checks (a list of mappings whose keys are not read until plan 23's check model, 23a FQ01). A `connection/1` file that has either section is refused with `DBD-CONN-009` naming the file and line and the fix `schema: connection/2`; a `connection/3` file is refused by its version (`DBD-110`). Files are written as `connection/1` until they use a section of format 2; `ConnectionDefinitionLoader.FormatOfText` is the format a published data source records. The generated JSON Schema (`JsonSchema()`) lists both sections.
