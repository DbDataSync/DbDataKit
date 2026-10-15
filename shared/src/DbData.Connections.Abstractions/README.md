# DbData.Connections.Abstractions

Contracts for Plan 06 (`docs/plans/06-connections.md`): the shared connection definition, provider interfaces, credential policy and the failure model. No client libraries.

## Contract

- `ConnectionDefinition` (schema `connection/1`): `engine`, `dialect`, `access` (`readonly|readwrite`), `address` XOR `connection_string` XOR `connection_string_secret`, `auth`, `read_login`, `write_login`, `tls`, `timeouts`, `properties`, `extensions`.
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
