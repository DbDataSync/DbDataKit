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

## One order for every product

`StateSettingsResolver.Resolve(new StateSettingsInputs(settingsFile, engineFlag, ..., engineVariable, connectionVariable, environment))` is the order every product reads its state settings in (plan 29b C07): the flag, then the environment variable, then the `state:` section of the product's settings file (read strictly through `StateSettingsReader`; an error names the file and, for a misspelt key, the one that was meant), then the default (SQLite). The engine and the connection each come from the first place that names them; `registry`, `namespaces` and `retention` come from the file. The result (`ResolvedState`) says where each part came from (`StateSources`), which is what `state show` prints. The file is never created. The Proxy keeps its own layering of file, environment and command line and reports through the same types (`ProxyStateConfig`).

## Writing the section

`StateSettingsWriter.Write(path, settings)` replaces only the `state:` block of a settings file (creating the file or the block when absent), validates it through `StateSettingsReader` first and renames a temporary file over the original, so an invalid setting is refused (naming the file and the key) and a crash leaves the old file. The rest of the file, its comments and line endings are kept; comments inside the old `state:` block are not. `Remove(path)` takes the block out; `Load(path)` reads it. Connection strings are written as given, secret references included.
