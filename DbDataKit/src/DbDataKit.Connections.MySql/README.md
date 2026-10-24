# DbDataKit.Connections.MySql

MySQL and MariaDB provider over MySqlConnector.

- `MySqlComposer`: `MySqlConnectionStringBuilder`; `SSL Mode=VerifyFull` by default, `Required` when the connection trusts or does not verify, `None` only for `tls.encrypt: false`; `tls.ca_file` is `SSL CA`. MySQL has no integrated flag (an authentication plugin is server configuration), so integrated mode writes nothing but the user name.
- Read-only recipe: `SET SESSION TRANSACTION READ ONLY` after open (`ReadOnlyEnforcement.SessionDefault`). A session can undo it; use a read-only account for enforcement.
- `MySqlFailureMapper` maps `MySqlException.Number`, then the SQLSTATE.

Tests are offline.
