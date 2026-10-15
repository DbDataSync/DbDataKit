# DbData.Connections.Postgres

PostgreSQL provider over Npgsql 10.0.3.

- `PostgresComposer`: `NpgsqlConnectionStringBuilder`; `SSL Mode=VerifyFull` by default, `Require` when the connection trusts or does not verify, `Disable` only for `tls.encrypt: false`; `tls.ca_file` is the root certificate. Npgsql has no integrated flag (GSS is negotiated when no password is set), so integrated mode writes nothing and relies on the stripped keys.
- Read-only recipe: `Options=-c default_transaction_read_only=on` at startup plus `SET default_transaction_read_only = on` after open (`ReadOnlyEnforcement.SessionDefault`). A session can undo it, so use a read-only role and the Plan 05 classifier for enforcement. SQLSTATE `25006` (a write in a read-only transaction) maps to a permission failure.
- `PostgresFailureMapper` maps `PostgresException.SqlState`; network and TLS failures come from the generic classifier through Npgsql's inner exceptions.

Tests are offline. Live (read-only session blocks a write): set `DBDATA_TEST_POSTGRES` to a full connection string.
