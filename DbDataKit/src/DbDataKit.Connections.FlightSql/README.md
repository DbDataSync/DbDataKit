# DbDataKit.Connections.FlightSql

An Arrow Flight SQL client shaped as ADO.NET: `FlightSqlConnection`, `FlightSqlCommand`, `FlightSqlDataReader`, `FlightSqlParameter`.

- `FlightSqlSettings`: address (host and port, or `grpc://`, `grpc+tcp://`, `grpc+tls://`, `http(s)://`), authentication (none, basic through the Flight handshake, or a bearer token), certificate validation, call headers for a proxy's data source and result compression, and prepared or inline parameters.
- `FlightSqlFailureMapper` maps a gRPC status to the shared failure kinds (`IFailureMapper`).
- Inline parameters need the caller to say how a value is written as a literal in the engine behind the server (`Func<object?, string>`); prepared parameters bind Arrow values and never become SQL text.
- The reader reads `DoGet` itself so LZ4 and Zstd batches decode.

A product turns its own connection settings into a `FlightSqlSettings`; nothing here knows about any product's configuration.
