# DbDataKit.Connections.SqlServer

SQL Server provider over Microsoft.Data.SqlClient 7.1.1.

- `SqlServerComposer`: `SqlConnectionStringBuilder`; `Encrypt=Mandatory` and `TrustServerCertificate=False` by default (the old Sync `TrustServerCertificate=true` default is gone); the host and port become `host,port`; `tls.ca_file` maps to `Server Certificate` (a pin, not a CA bundle); connect and command timeouts are always written.
- Read-only recipe: `ApplicationIntent=ReadOnly` only. This is a routing hint to a readable secondary, **not enforcement**; the control is a dedicated read login (`ReadOnlyEnforcement.Routing`).
- `SqlServerFailureMapper`: error number to kind (login 18456/4060, timeout -2, network 53/10061, permission 229, syntax 102/208, TLS SNI codes); the message is never used.
- Token logins set `SqlConnection.AccessToken` and strip user, password and integrated keys.

Tests are offline (built strings, hostile credentials, pass-through stripping, an unreachable-server classification). Live: set `DBDATA_TEST_SQLSERVER` to a full connection string.
`System.Security.Cryptography.Pkcs` is pinned centrally to 10.0.13 (transitive advisory).
