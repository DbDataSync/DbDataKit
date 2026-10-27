# DbDataKit.Serving.FlightSql

The Arrow Flight SQL module of the serving layer (plan 20; plan 19, N02): the wire protocol and nothing else. It is written against the core services of `DbDataKit.Serving.Abstractions` (identity, sessions, queries, catalog, updates), so any host that offers them can serve Flight SQL;
Proxy is the first. The module authenticates a handshake, hands back a token, and turns Flight SQL commands into service calls and the answers back into Flight streams. It does not know what a data source is, how a login is checked, which statements are allowed or how a query runs.

This is being moved out of Proxy in steps with Proxy's Flight tests as the net. So far: the compression of a result stream, the status a client sees for a failure, parsing basic credentials, and the stores for prepared and pending statements.
