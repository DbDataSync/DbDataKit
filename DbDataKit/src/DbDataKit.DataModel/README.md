# DbDataKit.DataModel

The `model/1` file format of master data models (plan 15, section 3; plan 19, Z02): entities with attributes, keys, references, rules and history, read strictly through `DbDataKit.Config.Yaml`.

`DataModelLoader.Load(yaml, file)` returns the model and the diagnostics. A problem names the file, line and column, uses the suite's diagnostic shape and, for a key that is nearly right, says what was meant. A file with a newer `schemaVersion` is refused (`DBD-110`) and a missing or invalid one is refused (`DBD-111`).

**Format 2 (`schemaVersion: 2`, plan 24a UC03).** `model/2` adds two optional root sections to `model/1`; a `model/1` file is read as before. `cache:` (`enabled`, `ttl` such as `5m`, at most `24h`, `scope` of `caller`, `rules` or `shared`, `dataset`: an entity) turns on the serving layer's result cache for the model (off unless `enabled: true`; the cache service itself is UC12). It is refused, naming the file and line, on an entity that keeps `effective_dated` history, because a read of it meets writes of other dates in flight. `checks:` is the model's data checks, a list of `check/1` entries read by `DbDataKit.Quality` (23a FQ01: the seven kinds, `severity`, `on_error`; a bad kind, a range with `min` above `max`, a referential check with no target and the like are refused with file and line); `DataModel.Checks` holds them. A `model/1` file that has either section is refused with `DBD-MDL-007` and the fix `schemaVersion: 2`, and a program that reads only `model/1` refuses a `model/2` file by its version (`DBD-110`): that is the clean break, and it is why the format of a published definition is the file's `schemaVersion` (`DataModel.SchemaVersion`). Adding keys to these sections later is additive and needs no new format.

What is checked:

- `name` and every entity and attribute name are plain identifiers; `storage.connection` is present; `approval.changes` is `none`, `steward` or `owner`.
- Types are SQL names (`VARCHAR(10)`, `DECIMAL(5,4)`, `BOOLEAN`, `TIMESTAMPTZ`, ...) or the canonical text of `DbDataKit.Dialect` (`string(10)`); `Canonical` on the result is what they mean.
- Every entity has a key of attributes it declares; key attributes are required.
- `pattern` compiles; `min` and `max` are numbers and `min` is not above `max`; `allowed` is a list; `default` is a value the type can hold.
- A reference names an entity of this model (or another `model`) and, within this model, an attribute that exists and is the entity's whole key or a unique attribute.
- `history` is `none`, `audit` or `effective_dated`; `role` is `table`, `dimension`, `mapping` or `calendar`.
- Rules are `sum_equals`, `no_overlap` or `no_cycle` with the attributes they need, all of which exist.

## Storage (plan 19, Z03)

`ModelStorage.Plan(model, "sqlserver" | "postgres")` returns the statements that make a model's storage: a schema named like the model, a table per entity (columns in the engine's own types from the shared dialect, NOT NULL for a required attribute or a key attribute), a primary key, a unique constraint per unique attribute, and a foreign key for each reference to an entity of the same model (the type must be the same at both ends). A reference to another model's entity is checked on a write, with no foreign key, and the plan says so in `Notes`. The statements are `DbDataKit.Schema`'s `TableDdl`, so a later slice that changes a structure starts from the same text.

Every entity table also has a `_version` column (the version a write names, plan 19 Z04) and the schema has an `_audit` table (`ModelStorage.AuditColumns`) that the write path fills. `ModelStorage.CreateAsync(connection, plan)` runs them in one transaction. It only creates: a table that is already there refuses the whole creation (`StorageException`), writing nothing. Defaults, ranges, patterns, allowed values and the cross-row rules are the write path's (`DbDataKit.Store`, Z04), and the columns history needs (Z05) are added then; the database holds the shape and the keys.

For an entity with `history: effective_dated` the table has `_valid_from` and `_valid_to`, the primary key is the key and `_valid_from`, one version of a key is open at a time (a filtered unique index), a unique attribute is unique among the open versions, and a reference to such an entity is checked on a write with no foreign key. This library says what a valid model is and how it is stored.
