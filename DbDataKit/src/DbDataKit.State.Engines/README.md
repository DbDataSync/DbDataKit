# DbDataKit.State.Engines

Opens a product's state database (`DbDataKit.State`) on the engine its operator names (plan 13a, P11).

```csharp
var state = StateOpener.Open(new StateSettings(engine, connectionString), Path.Combine(dataDirectory, "state.db"), schema, secretResolver);
```

* `sqlite` (the default) is a file in the product's own directory.
* `mssql` and `postgres` are a database on a server the operator names. The connection string may carry its password as a secret reference (`Password=secret:state-db`); a literal one works but is the operator's choice.
* The engine is chosen once: the schema is created on first open and there is no migration between engines (changing the engine starts an empty store).
* Two products on one server need a database each: the console's and the Proxy's tables carry the same names.
