# DbDataKit.Connections

Registry, composers, login resolution, parameter adapter and pool for Plan 06.

## Contents

- `ProviderRegistry`: engine id to `IDbProvider`; ids and aliases are case-insensitive, `odbc:name` resolves to `odbc`.
- `DbProviderBase`: dialect lookup, `OpenAsync` (create, open, apply the read-only recipe for read-only contexts, classify failures), `TestAsync`. `GenericAdoProvider` works over any `DbProviderFactory`.
- Composers (`ComposerBase` pipeline): base string, descriptor fixed properties, address, TLS, timeouts, `properties` (checked against `CredentialPolicy`), read-only keys, then **credentials last**. The rendered string is parsed again to prove user and password survive quoting unchanged. `BuilderComposer` is for provider builders (SqlClient, Npgsql), `GenericAdoComposer` for any key spelling (with an `OdbcQuoting` flag that writes braces instead of double quotes: Sync 9.8), `OdbcComposer` for ODBC (strict parse, `OdbcConnectionPolicy` driver/DSN allow-lists, no FILEDSN/SAVEFILE, driver profiles for SQL Server and PostgreSQL TLS and read-only keys; the legacy Windows driver literally named `SQL Server` has no profile because it knows none of those keys).
- `ApplyPassThroughCredentials`: strips every integrated-security key (Trusted_Connection, Integrated Security, Authentication, Kerberos and GSS keywords, client certificates, passfile), sets the client's user and password, asserts none remain, caps lengths, refuses control characters. Hostile input such as `x};Driver={evil};UID={` stays one value.
- `LoginResolver`: read order is `read_login`, `DBDATA_<APP>_<CONN>_READ`, legacy `DBDATABUILD_<CONN>_READ` (Build only, through the Secrets `LegacyEnvProvider`), then `auth`. Write mirrors it; write on a read-only connection is refused; read never looks at anything write.
- `ParameterStyleAdapter`: `@name`, `:name` or `$n` to `?` with the Plan 05 lexer (markers in strings, comments and quoted identifiers are left alone), returns the binding order; `PositionalCommand.Bind` adds one parameter per marker.
- `ConnectionPool` / `ConnectionLease`: global and per-user concurrency caps, acquire timeout (`PoolAcquireTimeoutException`), default command timeout, reuse of opened connections with idle reaping, metrics. It sits on top of the provider's own pool. `ConcurrencyGate` is the limit part on its own (global and per-user caps, acquire timeout, tickets): the pool uses it, and a consumer can use it when what needs limiting is not a pooled `DbConnection` (Proxy: a streaming result, work handed to the 32-bit worker). Extracted for Proxy.

## Decisions

- SqlServer and Postgres composers live in their engine packages (they need the client builders); the ODBC and generic ones are here because they need no client library.
- The pool's idle reuse must not cross pass-through users: use one pool per credential or `MaxIdle = 0`.
- Engines with no client-side builder behaviour (Npgsql `Remove` leaves some keywords in the string) are handled in `BuilderBag.Remove`, which rebuilds and fails closed.
