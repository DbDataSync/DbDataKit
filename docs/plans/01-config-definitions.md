# Plan 01: Definition / Config (`DbData.Config`)

Status: proposed. Depends on: none (Plan 02 supplies secret references; Plan 08 calls the migrator).
Inputs: review detail docs, sections "Configuration and definition schemas" in each.

## 1. Problem

| | Sync | Proxy | Build |
|---|---|---|---|
| Format | YAML typed POCOs via YamlDotNet; hand-written polymorphic converters | `appsettings.json` bound with `Get<ProxyOptions>()` | YAML via `StrictYamlReader` (event-level, no deserializer), hand-written loaders |
| Unknown keys | **ignored** (`IgnoreUnmatchedProperties`, `YamlConfigSerializer.cs:31`), so typos vanish on next save | ignored | **error** with coded diagnostic |
| Validation | at save time (`ConfigValidation`) | **none at startup** (no `IValidateOptions`) | coded `Diagnostic`s, fuzz-tested |
| Versioning | none | none | none (no `version:` key; relies on tool version, closed key sets, hashes) |
| Layering/overrides | `dbdatasync.config.yaml` then env | json + env + CLI | project `dbdatabuild.yml` + folder `_dbdatabuild.yml` merge (`YamlMerge`: lists append, maps merge, `key=`/`key-`/`key+`) |
| Editing | rewrite whole file; git auto-commit | hand edit | surgical editor (`Define`) preserves formatting |
| Schema | C# only | C# only + example json | JSON Schemas as editor aids, output schema enforced by tests |

Goal: one library giving strict parsing, typed binding, layering/overrides, versioned migrations, diagnostics,
and schema generation, usable from all three without forcing a single file format on application settings.

## 2. Scope

In: strict YAML reader and node tree with positions; diagnostic model; typed binder; layering; override
sources (env, CLI); schema versioning and migrations; round-trip editing; JSON Schema generation;
`Microsoft.Extensions.Configuration` provider plus `IValidateOptions` glue.
Out: product entity models (connection, mapping, model) which stay in the products; Plan 06 owns the shared
connection definition schema (built on this library); secrets storage (Plan 02).

## 3. Design

### 3.1 Parsing core (lift from Build)
`StrictYamlReader`, `YamlNodes`, `YamlFieldReader`, `YamlMerge` from `DbDataBuild.Models/Yaml`. Rules kept:
low-level parser events only, scalars stay strings, duplicate keys error, no anchors/aliases/merge keys/tags,
single document. Decouple from Build's `DiagnosticCatalog` by introducing:

```csharp
record ConfigDiagnostic(string Code, Severity Severity, string Path, int Line, int Column,
                        string Message, string? Fix);
interface IDiagnosticCatalog { ConfigDiagnostic Create(string code, YamlNode at, params object[] args); }
```
Each product registers its own code prefix (`DDB-`, `DDS-`, `DDP-`); shared codes use `DBD-`.
Keep Build's rule that every code has a fixture and fix text (enforced by a test helper shipped in the package).

### 3.2 Typed binding
Attribute- or source-generated schema (`[ConfigKey("connect_timeout")]`, required, default, enum, range,
`[Secret]`, `[Deprecated("use x", since:"2")]`) producing both the binder and the JSON Schema. Do not use
YamlDotNet's object deserializer (keeps Build's safety property). Polymorphic nodes (Sync's
`BatchReloadSegment`, `DeleteGuard`, `AfterChangeStrategy`) use an explicit discriminator
(`[ConfigUnion("kind")]`) replacing the hand-written converters.
Unknown-key policy is per schema: `Strict` (default, error with did-you-mean) or `Preserve`
(kept in an `Extra` bag and written back unchanged). Sync moves from "ignore" to `Preserve` first, `Strict` later.

### 3.3 Layering and overrides
Fixed precedence, lowest to highest: built-in defaults, shared/folder layer files, project file, environment,
CLI. Merge operators are Build's (`key=` replace, `key-` remove, `key+` append). Environment mapping:
`DBDATA_<APP>__Section__Key` (double underscore as separator) plus per-product legacy aliases
(`DBPROXY_Proxy__...`, `DbDataSync__App__RepoRoot`). A mandatory `config show --origin` command prints
each effective value with its source (file:line, env var, default) and masks `[Secret]` keys.
`Microsoft.Extensions.Configuration` provider: `AddDbDataYaml(path, schema)` uses the strict reader, so
`IOptions` binding works for Sync/Proxy host settings. Reload (Proxy's `IOptionsMonitor`) supported.
JSON stays supported as an input format behind the same node tree (Proxy can keep `appsettings.json`).

### 3.4 Versioning and migrations
- Every definition file carries `schema: <kind>/<int>` or `schemaVersion: <int>` (default 1 when absent, so
  existing files are valid). Newer-than-supported versions are refused with a clear upgrade message.
- `IConfigMigration { string Kind; int From; int To; void Apply(YamlDocument doc, IMigrationLog log); }`
  operating on the node tree with a surgical editor, so comments and ordering survive.
- Run modes: `config migrate --dry-run` (unified diff and diagnostics), `--apply` (writes `.bak` or makes a
  git commit when in a repo, which fits Sync's repo-of-config model), and in-memory migrate-on-read for
  read-only consumers. Migrations are pure functions with golden-file tests (before/after pairs).
- Migrations chain (1 to 2 to 3). A migration may emit questions (Build's answer-file model) when intent is
  ambiguous; non-interactive runs fail rather than guess.
- Tool/config compatibility is recorded in the release manifest (Plan 08) so the updater can decide whether
  migration is required and take a backup first.
- Rollback: backups are retained with the version of the writing tool; `config migrate --down` is not
  provided, restore from backup instead.

### 3.5 Round-trip editing
Take Build's `Define` surgical YAML editor as the base (`DbDataBuild.Define`); expose `YamlEditor.Set(path, value)`,
`Remove`, `Insert`, preserving comments/blank lines/key order. Sync's UI saves use it instead of reserializing, which reduces git
diff noise. Entities without comments may still be generated from the typed model.

### 3.6 Schema outputs
`dbdata-config schema --kind connection` emits JSON Schema (editor aids, as Build does). A test helper
asserts schema and binder agree (every binder key is in the schema and vice versa). Docs tables are
generated from the same attributes (Proxy's missing options reference).

### 3.7 Startup validation
`AddDbDataOptions<T>()` = bind, run validators, `ValidateOnStart`, and print all diagnostics at once with line
numbers. Cross-reference checks are a first-class concept (`IConfigValidator<T>` with access to the whole
document), e.g. Proxy `Users[].DataSources` must exist in `DataSources`; unknown `Provider`; invalid
`Bitness`; `Listen.Address` parse. Sync's `ConfigValidation` rules (names, addressing XOR, kind combinations)
become validators on the shared schema.

## 4. Package layout

```
DbData.Config.Yaml        strict reader, nodes, merge, editor, diagnostics (no other deps beyond YamlDotNet)
DbData.Config             schema attributes, binder, layering, migrations, env/CLI sources, JSON Schema
DbData.Config.Hosting     Microsoft.Extensions.Configuration/Options integration, validators, show --origin
DbData.Config.Testing     fixture/fuzz helpers, golden migration helper, schema-binder agreement test
```

## 5. Phases

| Phase | Work | Exit criteria |
|---|---|---|
| 0 | Decide key conventions (`snake_case` vs `camelCase`: Build uses snake_case, Sync camelCase), version key name, env prefix scheme. Write the spec. | Spec merged in `docs/` |
| 1 | Extract `DbData.Config.Yaml` from Build with its fuzz tests; Build consumes the package (behaviour-identical: its test suite is the safety net) | Build tests green, no golden diffs |
| 2 | Diagnostics + typed binder + schema generation; port one small Build config (`dialects:`) onto it | Binder/schema agreement test |
| 3 | Layering, env/CLI, `show --origin` | Precedence tests incl. legacy aliases |
| 4 | Migration engine + `schemaVersion` introduced as version 1 in all three; first real migration used as the proving ground | Dry-run/apply/backup tests; refusal of newer |
| 5 | Proxy adopts (`IValidateOptions` startup validation: fixes the gap in its review) | Proxy fails fast on bad config with line numbers |
| 6 | Sync adopts for `dbdatasync.config.yaml` and entity files, `Preserve` unknown keys then `Strict` | No config churn in existing repos; git diffs unchanged |

## 6. Risks and decisions

- **Naming convention clash** (snake vs camel): allow per-schema naming policy; do not rename existing keys.
- **Sync's typed model is large** (many entities, converters): migrate by entity, keep old serializer behind
  the same interface until each entity is ported.
- **Build's diagnostic catalog coupling**: keep the catalog pattern, make it injectable.
- **Comment preservation** across migrations is the hardest part of the editor; budget time, and fall back to
  a clear "reformatted" notice with the `.bak` kept.
- **Performance** is irrelevant at these sizes; correctness and error quality dominate.
- Decision needed: do migrations live in the shared repo or the product? Recommendation: engine shared,
  migrations in the product that owns the file kind; shared kinds (connection) migrate in the shared repo.

## 7. Acceptance

All three products load config through the shared strict reader; typos are errors everywhere; every definition
file kind has `schemaVersion`; `config migrate` exists with dry-run and backup in each product; `show --origin`
works; Proxy validates at startup; JSON Schemas are generated, not hand-written.
