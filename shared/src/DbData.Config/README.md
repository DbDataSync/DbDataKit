# DbData.Config

Typed, strict, layered configuration on top of `DbData.Config.Yaml`. No YamlDotNet object deserializer: everything goes through the strict node tree.

## Schema attributes (single source for binder, JSON Schema and origin report)

- `[ConfigSchema("kind", Version = 2, UnknownKeys = Strict|Preserve, Naming = CamelCase|SnakeCase|Verbatim, VersionKey = "schemaVersion")]` on a class. Undecorated classes: Strict, camelCase, no version key.
- `[ConfigKey("name", Required, Default, Min, Max, Pattern, Description)]`, `[Secret]`, `[Deprecated("use x", Since = "2")]`, `[ConfigIgnore]`, `[ConfigExtra]` (bag for unknown keys under Preserve).
- `[ConfigUnion("kind")]` on an abstract base with `[ConfigUnionCase("rows", typeof(RowsSegment))]` replaces hand-written polymorphic converters.
- Config types need a public parameterless constructor and public settable/init properties. Supported: string, bool, integers, floating point, decimal, enums (case-insensitive, `_`/`-` ignored), `TimeSpan` (`30s`, `5m`, `2h`, `1d`, `250ms`, `hh:mm:ss`), Guid, Uri, DateTime(Offset), nullable of these, `List/array/IReadOnlyList<T>`, `Dictionary<string,T>`, nested classes, unions.
- Booleans are `true`/`false` only (as in Build). A plain `null`/`~`/empty value is "no value": fine for nullable types, an error otherwise.

## API

- `ConfigBinder.Bind<T>(node, file) -> BindResult<T>` (Value, Diagnostics, Sources path to node). Reports every problem, not the first. Unknown keys: Strict gives `DBD-104` with did-you-mean; Preserve stores them in the `[ConfigExtra]` bag. Deprecated keys give a warning. The version key is accepted at the top level of versioned schemas.
- `IConfigValidator<T>` + `ConfigValidationContext`: whole-document cross-reference checks; `Error(path, message)` is located at the bound node (or nearest parent).
- `ConfigBuilder<T>`: `AddDefaults`, `AddText`, `AddFile(path, optional)` (file layers in the order added, lowest first), `AddEnvironment(app, legacyAliases)`, `AddCommandLine(args)`, `AddValidator`, `Build() -> ConfigResult<T>` (`Value`, `Diagnostics`, `Merged.Origins()`, `ValueOrThrow()`).
  Precedence: defaults < file layers < environment < command line. File layers use the Build merge operators (`key=`, `key-`, `key+`; lists append). Environment and CLI override single values (lists by index, or a comma list for lists of scalars).
- Environment: `DBDATA_<APP>__A__B` (case-insensitive; keys match existing/schema keys ignoring case, `_`, `-`; numeric level indexes a list). `EnvAlias("DBPROXY_", "Proxy")` and `EnvAlias("DbDataSync__")` keep legacy names working; the canonical name wins when both are set.
- CLI: `--set a.b=value`, `--a.b=value`, `--a.b value` (options with a dot in the name); the rest is returned untouched.
- Versioning: a missing version key means 1. A version newer than the schema/engine knows is refused (`DBD-110`) with an upgrade message.
- Migrations: `IConfigMigration { Kind, From, To, Apply(YamlDocument, IMigrationLog) }` (must advance by exactly 1). `MigrationEngine.Plan` (dry-run: new text, unified diff, log, questions; also in-memory migrate-on-read via `MigrateOnRead`), `Apply(kind, path)` writes `name.bak` (then `.bak.1`, ...) first and replaces atomically. The engine writes the new version key (inserted at the top, below the header comments, when absent). `log.Ask(id, question, options)` implements the answer-file model: an unanswered question fails the run, nothing is guessed. `YamlDocument` edits through `YamlEditor`, so comments/ordering survive; `RenameKey` is provided. No down migrations: restore the backup.
- `JsonSchemaGenerator.Generate<T>()` / `ToJson`: draft 2020-12, `additionalProperties` false for Strict, `x-secret`/`writeOnly`, `deprecated`, `oneOf` for unions, enum values in camelCase.

## Decisions

- Key naming: per-schema policy (default camelCase, Sync's convention; Build uses `SnakeCase`). Existing keys are never renamed; use `[ConfigKey("name")]`.
- Version key name: `schemaVersion` (integer). The `schema: <kind>/<int>` header is accepted as an alternative on versioned schemas (binder, builder, migration engine, JSON Schema `schema` property with a `^kind/N$` pattern); the migration engine keeps whichever form the file uses, and refuses a file that has both, a wrong kind, or a newer version. `SchemaHeader.TryParse/Format`.
- Env prefix: `DBDATA_<APP>__...`. Legacy aliases are caller supplied.
- Migrations live in the product that owns the file kind; the engine is shared (plan recommendation).
- Git-commit-instead-of-.bak is not implemented (only `.bak`).
- JSON input: files named `*.json` (via `AddFile`/`AddText`, and the MEC provider) go through `JsonInput` in `DbData.Config.Yaml`: JSON check (comments and trailing commas allowed, like appsettings.json), then the same strict node tree with line/column. Test helpers live in `DbData.Config.Testing`.
- Merge: top-level keys of every layer are layered; per-key opt-out of list appending is via `key=`.
