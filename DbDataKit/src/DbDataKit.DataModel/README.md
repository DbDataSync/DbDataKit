# DbDataKit.DataModel

The `model/1` file format of master data models (plan 15, section 3; plan 19, Z02): entities with attributes, keys, references, rules and history, read strictly through `DbDataKit.Config.Yaml`.

`DataModelLoader.Load(yaml, file)` returns the model and the diagnostics. A problem names the file, line and column, uses the suite's diagnostic shape and, for a key that is nearly right, says what was meant. A file with a newer `schemaVersion` is refused (`DBD-110`) and a missing or invalid one is refused (`DBD-111`).

What is checked:

- `name` and every entity and attribute name are plain identifiers; `storage.connection` is present; `approval.changes` is `none`, `steward` or `owner`.
- Types are SQL names (`VARCHAR(10)`, `DECIMAL(5,4)`, `BOOLEAN`, `TIMESTAMPTZ`, ...) or the canonical text of `DbDataKit.Dialect` (`string(10)`); `Canonical` on the result is what they mean.
- Every entity has a key of attributes it declares; key attributes are required.
- `pattern` compiles; `min` and `max` are numbers and `min` is not above `max`; `allowed` is a list; `default` is a value the type can hold.
- A reference names an entity of this model (or another `model`) and, within this model, an attribute that exists and is the entity's whole key or a unique attribute.
- `history` is `none`, `audit` or `effective_dated`; `role` is `table`, `dimension`, `mapping` or `calendar`.
- Rules are `sum_equals`, `no_overlap` or `no_cycle` with the attributes they need, all of which exist.

Storage, writes and history behaviour are other slices (Z03 to Z05); this library only says what a valid model is.
