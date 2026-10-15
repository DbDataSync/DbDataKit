# DbDataKit.Config.Hosting

Microsoft.Extensions.Configuration / Options integration for `DbDataKit.Config`.

## Contract

- `builder.AddDbDataYaml(path, schemaType?, optional, reloadOnChange)` / `AddDbDataYaml<T>(path, ...)`: provider using the strict reader. Any diagnostic makes `Load` throw one `InvalidDataException` listing all problems with file:line:column. With a schema type the file is also bound strictly (unknown keys with did-you-mean, types, ranges, required). Scalars flatten to `a:b:0:c` keys, so standard `IOptions` binding works. `reloadOnChange` uses a debounced `FileSystemWatcher`; a bad edit keeps the previous good data.
- `configuration.GetOrigins()`: key path to `KeyOrigin(File, Line, Column)` for DbData YAML providers in the root.
- `services.AddDbDataOptions<T>(section)`: `AddOptions<T>().Bind(section).ValidateOnStart()` plus `ConfigSchemaValidator<T>` (`IValidateOptions<T>`): checks `[ConfigKey]` required/range/pattern recursively and runs every registered `IConfigValidator<T>`; all failures are returned at once, prefixed with `file:line` when the origin is known.
- `ShowOrigin.From(ConfigResult<T>)` gives `OriginEntry(Path, Value, Source, Kind, Line, Secret)` for every effective scalar (file:line, `env:NAME`, `cli:...`, `defaults`, or built-in default); `[Secret]` values are replaced by `********`. `ShowOrigin.Format` renders text for a `config show --origin` command.

## Notes

- Environment variables with the `DBDATA_<APP>__` scheme and legacy aliases are handled by `DbDataKit.Config`'s `ConfigBuilder`; with plain MEC use the stock `AddEnvironmentVariables("DBDATA_PROXY__")`.
- JSON as an input format behind the same tree is not implemented (use the stock JSON provider).
- Warnings from validators do not fail startup.
