# DbData.Config.Yaml

Strict YAML subset reader, node tree with positions, layered merge, and surgical editor. Only dependency: YamlDotNet
(parser events only; no object deserializer is ever used). Lifted from DbDataBuild's `DbDataBuild.Models/Yaml` and `Define`
and decoupled from Build's diagnostic catalog.

## Contract

- `StrictYamlReader.Read(text, file, diagnostics, catalog?) -> YamlNode?`. Scalars stay strings; duplicate keys, anchors, aliases,
  tags, merge keys (`<<`), multiple documents and non-scalar keys are diagnostics. Never throws on damaged input (the YamlDotNet scanner's
  non-`YamlException` failures are caught). Empty file returns null with no diagnostics.
- Nodes: `YamlScalar` (`Quoted`), `YamlSequence`, `YamlMapping` (`Get`, `GetEntry`), all with 1-based `Line`/`Column`, source `Start`/`End` offsets and `Flow`.
- `ConfigDiagnostic(Code, Severity, Path, Line, Column, Message, Fix)` plus `KeyPath`. `Path` is the file. `ConfigSeverity` (named so it does not
  collide with a product's own `Severity`).
- `IDiagnosticCatalog` is injected: `DefaultDiagnosticCatalog` emits shared `DBD-` codes (`SharedCodes`); `PrefixedCatalog("DDB-")` maps them to a
  product prefix (or an explicit map) and can override severities. Every shared code has default fix text.
- `YamlMerge.Merge(layers, layeredKeys, diags)`: maps merge by key, lists append (repeated scalars once, mappings with `name` merge by name),
  scalars replace; `key=` replace, `key-` remove listed items/keys, `key+` merge. `MergedYaml.Origins()` gives path, file, line, value per scalar.
- `YamlFieldReader`: base class for loaders (`CheckKeys` with did-you-mean, `Scalar`, `StringList`), `DidYouMean`.
- `YamlEditor.Set/Remove/Insert(text, path, ...) -> EditResult(Text, Problem)`: splices only the characters needed, so comments, order,
  quoting and CRLF survive; the result is re-parsed and refused (`DBD-130`) rather than returned if invalid. Paths: `a.b[0].c`.
  Not editable by design: block scalars, documents that do not parse. `YamlValue` is text (quoted when plain would change meaning) or `YamlValue.Raw`.
- `UnifiedDiff.Create(old, new, path)`.

## Decisions / notes

- Plan names the severity enum `Severity`; implemented as `ConfigSeverity`.
- `Path` on the diagnostic is the file (Build's `SourceLocation.File`), the dotted key path is the extra `KeyPath`.
- Removing the only entry of a block mapping leaves `key:` (null) behind; Set/Insert then treat it as an empty value.
- Build's `ReadInfo`/`ReadColumns`/`ReadRewrites` helpers are product-specific and were not lifted.
- The "every code has a fixture and fix text" test helper is the `DBD-` defaults table plus tests here; the Testing package ships the reusable helper (later).
- `JsonInput.Read/ReadByExtension(text, file, diagnostics, catalog?)`: JSON behind the same node tree (strict JSON syntax check with JSON positions, then the strict reader; `.json` file names select it). Comments (`//`, `/* */`) and trailing commas are allowed; duplicate keys stay errors.
