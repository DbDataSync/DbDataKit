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

| `Ddl.TableDdl` (`TSqlTableDdl`, `PostgresTableDdl`) | the statements that create and change a table on SQL Server, Fabric and PostgreSQL, from columns already in the engine's own types (`NativeColumn`, `IndexSpec`); primary, unique and foreign keys added after the table (`AddPrimaryKey`, `AddUnique`, `AddForeignKey`, plan 19 Z03); the risk class of a type change |
| `Questions.*` | `Question`, `QuestionOption`, `Proposal`, `ResolvedAnswer`, `AnswerSerializer`, `QuestionText`, `QuestionIds`: the decisions a plan asks a person about, with ids that are the same every time |
| `Planning.StructurePlanner` | the decisions about a table's structure, a pure function of a `DesiredTable`, the live `ObjectShape` and the answers: `Baseline` (adopt an object the tool never recorded, stop for a change made behind its back), `Columns` (add, drop, rename, alter, each with its risk class and the shape hash it leaves), `Indexes` (create, rebuild, report), `Create` (a table that does not exist). Its output is `StructureStep`s and `StructureProblem`s; the product turns them into its own plan steps and codes |

What a product supplies: a `DesiredTable` (columns already mapped to the engine's types, declared renames, the indexes that apply), an `IStructureHost` (how to ask the person and where to note things), and `Func<string, bool>` for "the person allowed this change made outside the tool". Build's planner does that for its models and keeps the loads, hooks, copies and the plan file (plan 19, H02).

Still in `DbDataBuild.Targets` (H02/H04 next): the logical-to-native type table of each engine, and the reading of the actual structure through `DbDataKit.Metadata`.
