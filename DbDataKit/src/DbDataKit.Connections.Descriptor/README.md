# DbDataKit.Connections.Descriptor

YAML-described ADO.NET engines over a `DbProviderFactory`: the port of Sync's `driver.yaml`, with SQL facts moved to Plan 05's `dialect.yaml`.

```yaml
schema: engine/1
id: mssql-odbc
display_name: SQL Server via ODBC
library: odbc                  # resolved by an IProviderFactoryResolver
dialect: sqlserver             # a dialect id (built in, or added with DialectCatalog.With(yaml))
odbc_quoting: true
connection_string_keys: { host: Server, port: none, database: Database, username: UID, password: PWD,
                          connect_timeout: Login Timeout, integrated_security: Trusted_Connection,
                          encrypt_key: Encrypt, encrypt_on: "yes", encrypt_off: "no",
                          trust_key: TrustServerCertificate, trust_value: "yes", verify_value: "no" }
fixed_properties: { Driver: "ODBC Driver 18 for SQL Server", MARS_Connection: "no" }
overridable_properties: [MARS_Connection]
read_only: { keys: { ApplicationIntent: ReadOnly } }
```

Rules enforced by `DescriptorLoader`:

- `fixed_properties` apply first. Only keys named in `overridable_properties` can be changed by a connection's `properties`; the rest are locked, even against the connection string. `Driver` and `DSN` are the intended fixed keys and can never be made overridable.
- Credential, host, identity, authentication and TLS keys are refused as fixed properties. Sync's example `TrustServerCertificate: "yes"` is now a load error: a descriptor gives only the key spelling and the two values, the connection decides (default verify).
- Unknown dialect, unknown library, unknown keys (with a did-you-mean), and key names containing `; = { }` are errors.

`DescriptorRegistration.RegisterDirectory` loads `*.engine.yaml` and registers the good ones, returning diagnostics for the rest.

Not ported: type maps and catalog queries (the dialect owns types; catalogs are Plan 04), capability lists (Plan 07), JDBC and compiled-plugin `base` kinds.

## The engine registry (plan 14 R5, plan 19 J06)

`EngineRegistry.Builtin` is the suite's one list of engines: the `engines/*.engine.yaml` descriptors embedded in this assembly (`ansi`, `sqlserver`, `postgres`, `mysql`, `oracle`, `duckdb`, `fabric`, `odbc`, `flightsql`). A descriptor says, besides how to connect, what the suite knows about the engine: `aliases`, the `dialect` that writes its SQL, the `provider` (`DbDataKit.Connections`) and `catalog_reader` (`DbDataKit.Metadata`) that serve it, and a `roles` map with how far it is supported in each role of `EngineRoles` (Sync source and target, Build target and origin, Proxy source, semantic emitter, master data store): `supported`, `unverified`, `planned` or `no`, with a `note` that says why (required for `planned` and `unverified`).

- `EngineRegistry.Load` checks the documents as a set: valid descriptors, unique ids and aliases, a descriptor for **every** dialect in `DialectCatalog.Builtin`; a failure is an `EngineRegistryException` whose diagnostics name the file and line.
- The registry entries are metadata: they register no provider. `DescriptorRegistration.RegisterDirectory` is still how a YAML-described ADO.NET engine is added.
- `EngineRegistryTests` ties the registry to the code (a provider's engine and dialect, `ProviderRegistry`'s aliases, the catalog readers in `DbDataKit.Metadata`) and `EngineSupportTable` writes `docs/engine-support.md` from it; `EngineSupportTableTests` fails when the checked-in file is not the generated one (`UPDATE_GOLDEN=1` writes it).
