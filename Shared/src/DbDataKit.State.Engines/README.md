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
* Changing the engine in the settings alone starts an empty store. To move the state, use `state move` (or `state export` and `state import`), offline, which is verified and leaves the old database untouched (`DbDataKit.State` README, "Moving state between engines").
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

`StateSettingsResolver.Resolve(new StateSettingsInputs(settingsFile, engineFlag, ..., engineVariable, connectionVariable, environment))` is the order every product reads its state settings in (plan 29b C07): the flag, then the environment variable, then the `state:` section of the product's settings file (read strictly through `StateSettingsReader`; an error names the file and, for a misspelt key, the one that was meant), then the default (SQLite). The engine and the connection each come from the first place that names them; `registry`, `namespaces` and `retention` come from the file. The result (`ResolvedState`) says where each part came from (`StateSources`), which is what `state show` prints. The file is never created. The Gateway keeps its own layering of file, environment and command line and reports through the same types (`GatewayStateConfig`).

## Writing the section

`StateSettingsWriter.Write(path, settings)` replaces only the `state:` block of a settings file (creating the file or the block when absent), validates it through `StateSettingsReader` first and renames a temporary file over the original, so an invalid setting is refused (naming the file and the key) and a crash leaves the old file. The rest of the file, its comments and line endings are kept; comments inside the old `state:` block are not. `Remove(path)` takes the block out; `Load(path)` reads it. Connection strings are written as given, secret references included.

## Support matrix (plan 29b, U01)

| Engine | Supported (N-1 and N) | Newer | Older |
| --- | --- | --- | --- |
| SQL Server | 2022 (16.x) and 2025 (17.x) | allowed, noted as untested by `state test` | refused, naming 2022 as the oldest |
| PostgreSQL | 16 and 17 | allowed, noted as untested | refused, naming 16 as the oldest |
| SQLite | 3.35 and newer (the library comes with the product) | allowed | refused |

CI runs the State and Scheduling suites on both columns: the current engines (SQL Server 2025, PostgreSQL 17, the `mssql` and `postgres` profiles of `testenv/`) in the ordinary jobs and the older ones (`mssql2022`, `postgres16`) in the job `state-older-engines`. Locally: `testenv/testenv.sh up mssql2022 postgres16`, then `eval "$(testenv/testenv.sh env dbdatakit-older)"`.

**Features per module.** A module that cannot work without something the engine may lack declares it: `new StateModule(..., requiresFeatures: [EngineFeature.Vector])`. No shipped module needs one yet; the first is the knowledge index (plan 27).

| Feature | SQL Server | PostgreSQL | SQLite |
| --- | --- | --- | --- |
| `Vector` (vector columns and search) | 2025 or later (the `vector` type) | the `pgvector` extension, installed on the server (`pg_available_extensions`); the module's migration runs `CREATE EXTENSION IF NOT EXISTS vector` | none |

`EngineSupport.Features(engine, version, ...)` and `Features(connection, engine)` say which a server has (`Enabled`: usable; `Creatable`: installed on the server, not created in this database yet, which a migration does; `Missing`). `state show` prints them and `state test` has a `features` step. On PostgreSQL a `Creatable` extension also carries `CreateExtensionProblem`: the login is not a superuser and the extension is not trusted, or it has no `CREATE` on the database; `state test` and the migrator then refuse with that sentence before any DDL. The snapshot importer makes the same check (`SnapshotImportRefusal.FeatureMissing`) before it creates the first schema.

**How a refusal reads.** The migrator checks before it runs any DDL (no schema, no registry, no table is made), only for a module that still has migrations to apply, and stops with one sentence naming the module, the feature, the engine and the fix:

```
The module 'knowledge' needs the engine feature 'vector' (SQL Server 2025's vector type), and this SQL Server 2022 (16.0.4135.4) does not have it; nothing of the module was created. To fix it: use SQL Server 2025 or later for this install, or pick another engine (PostgreSQL with pgvector).
```

On PostgreSQL the fix is to install the pgvector extension on the server (the package for the PostgreSQL version, or the `pgvector/pgvector` image); on SQLite, to pick SQL Server 2025 or PostgreSQL with pgvector. `state test` fails with exit code 29 and the same sentence, so it is found before the service is started. A module already applied is not checked again: a start never fails for what already works.

## Rolling upgrade of a group (plan 29b, U02)

A group's instances (the Gateway's `serving_instance`, the runner's `instance` table) record their software version and their module versions (`name=version;name=version`) in their row. `state show` and `GET /api/v1/state` list the live instances with them; a row of a binary older than this feature has none (shown as not recorded).

Before `StateMigrator` applies a migration of a module to a group's database it asks the live instances (`IGroupInstanceSource`, supplied by the product when it opens the state: `StateOpener.Open(..., groupInstances: new StandardGroupInstanceSource())`; the default is none), under the same migration lock, and refuses with a `MigrationRefusedException` naming the instance (id, host, process, software and module version) when one cannot work with the new schema:

* a module declares how far back its instances may be with `StateModule.CompatibleWithPrevious` (default 0: the previous version is not compatible; 1: it is, because the migration only adds what the older code ignores, such as a nullable column). An instance works when its module version is at least the new version minus that number; an older one blocks.
* an instance whose row records no versions is taken to run the version the database is at.
* an instance that is not live (its heartbeat lapsed: 30 seconds for the Gateway, the lease for a runner; or a runner row marked gone) never blocks. An instance that starts later finds the newer module version and refuses to start, naming itself.

The upgrade order follows from that: when the module declares the previous version compatible, start the new instances while the old ones run (the first one migrates), then stop the old ones; when it does not, stop the old instances first, or stop the one the refusal names, then start the new ones. The author of a migration states the choice (`CompatibleWithPrevious`) for every migration they append; a destructive one (a dropped or renamed column, a changed meaning) keeps the default.
