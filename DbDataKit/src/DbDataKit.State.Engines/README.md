# DbDataKit.State.Engines

Opens a product's state database (`DbDataKit.State`) on the engine its operator names (plan 13a, P11), and reads the `state:` section of `server.yaml` (plan 29a, Q06).

```csharp
// built from modules (plan 29): each module is applied and versioned on its own
var settings = StateSettingsReader.Read(yaml.State);        // strict: a misspelt key is named, with the one that was meant
var state = StateOpener.Open(settings, Path.Combine(dataDirectory, "state.db"), [PayloadStore.Module, MyModule.Module], new StateScope(Group: "main"), secretResolver);
var retention = new RetentionJob(state, settings.Retention); // history is removed by one shared job

// the older form, one StateSchema per database, until every product has moved (plan 29a, Q25)
var state = StateOpener.Open(new StateSettings(engine, connectionString), Path.Combine(dataDirectory, "state.db"), schema, secretResolver);
```

* `sqlite` (the default) is a file in the product's own directory.
* `mssql` and `postgres` are a database on a server the operator names. The connection string may carry its password as a secret reference (`Password=secret:state-db`); a literal one works but is the operator's choice.
* The engine is chosen once: the schema is created on first open and there is no migration between engines (changing the engine starts an empty store).
* **Where the tables are** is configurable per module (a schema and a prefix, `{module}` and `{scope}` usable in both); the defaults are the schema `dbdatakit_<module>` on SQL Server and PostgreSQL and the prefix `dbdatakit_<module>_` on SQLite. Services that share one database find each other's modules through the registry table (`dbdatakit.modules`, `dbdatakit_modules` on SQLite). Changing a setting for a module that already exists is refused rather than creating a second set of tables.
* **The old structure is not converted.** A database made with one schema version for the whole database is refused with a message naming it; point the service at a new database and set it up again.
* The `state:` section:

```yaml
state:
  engine: postgres
  connection: Host=db;Database=dbdatakit;Username=svc;Password=secret:state-db
  registry:   { schema: dbdatakit }
  namespaces:
    default:  { schema: dbdatakit, prefix: "{module}_" }   # for every module with no entry of its own
    identity: { schema: ident }
  retention:
    days: 30                         # 0.25 is six hours
    modules: { runs: 7 }
    tables:  { runs.run: 14, audit.entry: forever }
```
