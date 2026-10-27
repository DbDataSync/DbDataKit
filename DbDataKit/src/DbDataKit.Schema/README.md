# DbDataKit.Schema

What a table's structure is, once, for every product that creates or changes tables (plan 19, lane H; plan 15, section 4). Moved out of `DbDataBuild.State` without a change of behaviour: Build's golden tests of its structure decisions (H01) and its tracking tables record the hashes, so a hash must not change.

| Type | What it is |
| --- | --- |
| `ColumnShape` | one column as a catalog or a definition reports it: name, engine type name, length, precision, scale, nullability, collation, computed text |
| `PhysicalItem` | an index, partitioning or compression setting |
| `ObjectShape` | a table or view (`ObjectKind`) with its columns in ordinal order and its physical items |
| `Hashing` | the shape hash (column names, types, sizes, nullability, collation, computed text, sorted by name so column order never changes it), the physical hash and the script hash; the canonical text each covers is built here and nowhere else |
| `Drift.Classify` | `Missing`, `Untracked`, `InSync` or `OutOfBand`, from the live object and the last recorded shape hash |
| `IndexText` | the engine-neutral text of an index definition and its parse |

Still in `DbDataBuild.Planning` and `DbDataBuild.Targets` (the rest of plan 19, H02, in later steps): the decision table and its data, the plan document types, the DDL rendering (H03) and the reading of the actual structure (H04, through `DbDataKit.Metadata`).
