# DbDataKit.Connections.Oracle

Oracle provider over Oracle.ManagedDataAccess.Core.

- `OracleComposer`: a host address is EZ Connect (`host:port/service`, port 1521 by default), written as `tcps://host:port/service` when `tls.encrypt` is on. With a connection string as the base the data source is the operator's and TLS is not rewritten. ODP.NET's builder cannot relax certificate checks, so `tls.verify: false` changes nothing; `tls.ca_file` and integrated authentication are refused with a message.
- No read-only session recipe exists (`ReadOnlyEnforcement.None`); use a read-only user.
- `OracleFailureMapper` maps `OracleException.Number` (ORA codes).

Tests are offline.
