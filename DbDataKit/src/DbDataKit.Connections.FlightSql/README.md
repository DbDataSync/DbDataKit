# DbDataKit.Connections.FlightSql

An Arrow Flight SQL client shaped as ADO.NET: `FlightSqlConnection`, `FlightSqlCommand`, `FlightSqlDataReader`, `FlightSqlParameter`.

- `FlightSqlSettings`: address (host and port, or `grpc://`, `grpc+tcp://`, `grpc+tls://`, `http(s)://`), authentication (none, basic through the Flight handshake, or a bearer token), certificate validation, call headers for a proxy's data source and result compression, and prepared or inline parameters.
- `FlightSqlFailureMapper` maps a gRPC status to the shared failure kinds (`IFailureMapper`).
- Inline parameters need the caller to say how a value is written as a literal in the engine behind the server (`Func<object?, string>`); prepared parameters bind Arrow values and never become SQL text.
- The reader reads `DoGet` itself so LZ4 and Zstd batches decode.

A product turns its own connection settings into a `FlightSqlSettings`; nothing here knows about any product's configuration.

Where the line is (owner review 13b, A19): this is a query client. Build's login grammar (`token_env`, `token_command`, `ca_file`, `header.<name>`) is parsed in Build into a `FlightSqlSettings` (the settings carry the CA file and call headers), and the catalog calls (tables, columns, keys through Flight SQL's metadata calls) stay in the product that needs them, as in Sync's driver. A product adds its own sign-in forms in front of the settings; it does not add them here.
