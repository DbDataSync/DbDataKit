# SYNCAUTHORING-19: Sync uses the shared types (work in progress)

## State

Step 1 (the list) is done; nothing in the code has changed. Five cases on the list change what other products answer (Build, Transfer, the Gateway,
the Store, the catalog screens), so they are proposals SYNCAUTHORING-P23 to SYNCAUTHORING-P27 in the subject file, awaiting the owner. Steps 2
(shared additions) and 3 (Sync reads and writes the shared type) wait for those answers.

The answers below were measured, not read off the code: a probe program built against `DbDataKit.Sync.Core` asked each Sync dialect and each shared
dialect for the same types (`Sync` is what `MsSqlDialect`, `PostgresDialect` and the others answer today; `shared` is what
`DialectCatalog.Builtin.Get(...)` answers, which Build, Transfer, the Gateway, the Store and the Metadata readers use).

## The list

### 1. Unsigned integers (a bug in Sync; the shared tables already answer)

The shared type keeps `Unsigned`; Sync's conversion (`SharedTypes.ToSync`) drops it, except DuckDB's reading, which widens at read time
(`ToSyncWideningUnsigned`).

**SQL Server's `tinyint` is read as a signed 8-bit integer by Sync.** The shared `sqlserver` table reads it as `int8(unsigned)`; `ToSync` drops the
flag and Sync's `MsSqlDialect` answers `Int8` (pinned by `MsSqlDialectCanonicalTypeTests`, `[InlineData("tinyint", CanonicalTypeKind.Int8)]`). What a
target gets for a SQL Server `tinyint` column today:

| Target | Sync today | Shared tables |
| --- | --- | --- |
| SQL Server | `smallint`, note "no signed 8-bit integer" | `tinyint` |
| PostgreSQL | `smallint` (widened) | `smallint` (widened) |
| MySQL | `tinyint` (signed: 128 to 255 fail on write) | `tinyint unsigned` |
| DuckDB | `tinyint` (signed: 128 to 255 fail on write) | `utinyint` |
| Oracle | `number(3)` | `number(3)` |

MySQL's unsigned types have the same fault: `int unsigned` becomes `int`/`integer` on every target (values above 2,147,483,647 fail),
`bigint unsigned` becomes `bigint` (values above 9,223,372,036,854,775,807 fail), `tinyint unsigned` becomes signed `tinyint` on MySQL and DuckDB.

DuckDB's widening at read time gives a narrower answer than the shared tables: `UTINYINT` becomes `smallint` everywhere (shared: `tinyint` on SQL
Server, `tinyint unsigned` on MySQL, `utinyint` on DuckDB); `UBIGINT` becomes `decimal(20,0)` everywhere (shared: `bigint unsigned` on MySQL, `ubigint`
on DuckDB, `numeric(20,0)`/`decimal(20,0)`/`number(20)` elsewhere, widened with a note).

Change: none to the shared tables. Sync keeps the flag and `ToSyncWideningUnsigned` goes. Products affected: Sync only. Sync's answers change for
these columns (named in the commit: `MsSqlDialectCanonicalTypeTests` tinyint, `DuckDbCanonicalTypeTests` `UTINYINT` → `Int16`, and the rendering
tests that build a signed `Int8` from a "tinyint"). Consequences inside Sync:
- `GenericValueBinder` and `SampleValues` bind by kind; an unsigned 64-bit value needs `decimal` (or `ulong`), an unsigned 8-bit one `byte`.
- An existing target made from such a source shows a type step in its alter plan the next time provisioning is planned: SQL Server `smallint` →
  `tinyint` (narrowing; every value fits since the source is `tinyint`), MySQL `tinyint` → `tinyint unsigned`, DuckDB `smallint` → `utinyint`. The
  plan is shown before it is applied; nothing changes on its own.

### 2. A time or timestamp with no stated precision (proposal SYNCAUTHORING-P23)

Sync's `MicrosecondsByDefault` turns a missing scale into 6 on PostgreSQL, MySQL and DuckDB. PostgreSQL's and DuckDB's shared defaults are 6 already,
SQL Server's 7 (Sync does not apply the rule there) and Oracle's 6, so the only differences are MySQL and the ANSI `time`.

Where Sync has a time or timestamp with no scale: the history columns of a historized target (`HistorizedProvisioning`, `valid_from`/`valid_to`), a
Flight SQL column (`time` and `timestamp` from Arrow carry no scale in their names), a `driver.yaml` type map entry, `ScriptTestService`'s columns.

| Type with no scale | MySQL, Sync today | MySQL, shared |
| --- | --- | --- |
| timestamp | `datetime(6)` | `datetime(0)` |
| timestamp with zone | `timestamp(6)` | `timestamp(0)` |
| time | `time(6)` | `time(0)` |

Example: a historized mapping into MySQL creates `valid_from datetime(6)` today; on the shared table it would be `datetime(0)`, and two changes in
the same second would get the same `valid_from`.

Products affected if the shared `mysql` table changes its default: the Store's `at` columns of a model on MySQL (`CanonicalType.Timestamp(null,
tz: true)` → `timestamp(0)` today), Transfer's tables created from a probed result (`TypeText`: a .NET `DateTime` reads as a timestamp with no
scale), Build's `MutationGate` (a `DbType.DateTime` parameter), and the ANSI `time` default (0) if ANSI follows.

### 3. A decimal with no precision (proposal SYNCAUTHORING-P24; a bug in Sync and in the shared tables)

PostgreSQL's unconstrained `numeric` reads, in both, as a decimal with no precision or scale (note: "unconstrained numeric has no fixed precision or
scale"). Sync's conversion (`SharedTypes.ToShared`) fills in 18 and 0, so a value 1.5 is stored as 2:

| Target | Sync today | Shared tables |
| --- | --- | --- |
| PostgreSQL | `numeric(18,0)` | unmappable |
| SQL Server | `decimal(18,0)` | `decimal(18,0)`, no note |
| MySQL | `decimal(18,0)` | `decimal(10,0)`, no note |
| Oracle | `number(18,0)` | unmappable |
| DuckDB | `decimal(18,0)` | unmappable |

The shared Arrow map already answers this case another way: no precision becomes `decimal(38,9)`, lossy, with a note. A `driver.yaml` entry with no
precision reaches the same code. Products affected: Transfer and the Store (SQL Server and MySQL round silently; PostgreSQL, Oracle and DuckDB
refuse), Sync.

### 4. Fixed-length binary (proposal SYNCAUTHORING-P25)

Sync keeps SQL Server's and MySQL's `binary(n)`, and SQL Server's `rowversion`/`timestamp`, as fixed-length (`IsFixed`); the shared type has no such
flag and reads them as variable-length with a lossy note.

| Source | Sync today (SQL Server / MySQL target) | Shared tables |
| --- | --- | --- |
| SQL Server `binary(16)` | `binary(16)`, no note | `varbinary(16)`, note "fixed-length binary becomes variable-length" |
| SQL Server `rowversion` | `binary(8)`, no note | `varbinary(8)`, note "rowversion becomes plain binary(8)" |

Other targets are the same in both (`bytea`, `raw(16)`, `blob`). Without a flag, only new targets change: the alter plan reads an existing
`binary(16)` back as the type it would write and proposes nothing. Products affected if the shared type gains a flag: Transfer (a SQL Server
`binary(16)` copied to SQL Server or MySQL creates `binary(16)` instead of `varbinary(16)`), the catalog the Workspace and the console show (the
canonical text of such a column), the Gateway's Arrow map (`FixedSizeBinary` reads as variable-length with a note today).

### 5. Oracle has no time type (proposal SYNCAUTHORING-P26)

| Canonical `time(6)` on Oracle | Sync today | Shared tables |
| --- | --- | --- |
| rendered | `timestamp(6)`, note "stored as TIMESTAMP, whose date portion is meaningless" | `interval day(0) to second(6)`, note "stored as an interval of the day" |
| read back | `timestamp(6)` → timestamp | `interval day(0) to second(6)` → `interval` (another kind) |

Sync's existing Oracle targets have `TIMESTAMP` columns for times. Products affected if the shared table changes: Transfer and the Store writing a
time column to Oracle.

### 6. ANSI type names: the Flight SQL driver and a generic target (proposal SYNCAUTHORING-P27)

Sync's `AnsiDialect` has its own type table, written for the names the Flight SQL driver gives an Arrow column (`ArrowValues.NativeTypeName`:
`tinyint`, `varbinary`, `time`, `varchar`, ...). The generic "Arrow Flight SQL" driver also provisions with it. The shared `ansi` table answers
differently:

| Native name | Sync reads | Shared `ansi` reads |
| --- | --- | --- |
| `tinyint` (Arrow int8) | int8 | unmappable |
| `varbinary` (Arrow binary) | binary | unmappable |
| `float`, `double`, `bool`, `text`, `char`, `binary` | float, double, boolean, string, string, binary | unmappable |
| `time` (Arrow time) | time, no scale | `time(0)` |

| Canonical type | Sync renders | Shared `ansi` renders |
| --- | --- | --- |
| string, no length | `VARCHAR(4000)`, note "capped at 4000" | `clob` |
| string(50) | `VARCHAR(50)` | `character varying(50)` |
| binary | `VARBINARY(4000)` | `blob` / `binary varying(n)` |
| time / timestamp | `TIME` / `TIMESTAMP` | `time(0)` / `timestamp(6)` |
| int8, unsigned, guid, json, xml | `SMALLINT`, ..., `CHAR(36)`, `VARCHAR(4000)` with notes | unmappable |

The shared `ansi` table is used by the Gateway's Flight SQL server (`SqlDialects.FlightSql`), Build's `ansi` connections, every ODBC dialect
(`OdbcDialect` extends it) and Transfer's fallback, so changing it changes their answers.

### 7. Interval and list columns (default, Sync only)

The shared kinds `Interval` (PostgreSQL and DuckDB `interval`, Oracle's intervals) and `List` have no Sync kind; Sync reads them as unmappable today
("no Sync equivalent of interval"), so provisioning refuses the column unless the mapping gives it a type override or a transform.

Default: Sync keeps refusing `Interval` and `List` columns, since its readers, staging and writers do not carry those values
(`GenericValueBinder` has no interval). Example: a mapping from PostgreSQL with a `duration interval` column still gets "Column 'duration' has no
cross-engine type mapping"; the author leaves it out or casts it to text in a transform. The alternative is to replicate PostgreSQL to PostgreSQL
intervals, which needs the value binding first.

### 8. The JDBC dialect (default, Sync only)

`JdbcDialect` (the pgJDBC spike) has its own table of PostgreSQL's catalog spellings (`int4`, `varchar`, `timestamptz`) and refuses to render int8,
binary, time, guid, json and xml. The shared `postgres` table reads every one of those spellings the same way, except `varchar` with no length
(Sync: `varchar(255)`; shared: unbounded `text`).

Default: the JDBC dialect reads and writes through the shared `postgres` table. Example: a JDBC source with a `bytea` column, refused today, is
provisioned as `bytea`.

### 9. A string or binary with no length that is not "max" (Sync only)

Sync's type has both `Length` and `IsMax`; a string with neither (a `driver.yaml` entry `varchar: String`, the JDBC `varchar`) is rendered as
`varchar(255)` / `nvarchar(255)` by `ToShared`. In the shared type a missing length means unbounded. Moving to the shared type, such a column
becomes `nvarchar(max)` / `text` / `longtext` on the target: wider, and no value is cut at 255 characters. The same for binary (255 today).

### 10. Shapes that differ without changing an answer (Sync only)

- **The note of a reading.** Sync's type carries `SourceNote`; the shared answer carries its fidelity and note beside the type
  (`TypeMapping<CanonicalType>`). Sync's columns (`ColumnMetadata`, `ProvisioningColumn`) carry the shared mapping, or the type and its note side
  by side; `RenderedColumnType` keeps joining the reading's note and the rendering's.
- **Kind names.** Sync's `TimestampTz` is the shared `timestamp(tz)`. A `driver.yaml` type map names Sync's kinds (`datetimeoffset: TimestampTz`,
  `Unmappable`); the descriptor keeps accepting those names and turns each into the shared type, so no driver file changes.
- **Scripts.** `IScriptDialect` and `ILifecycleHook` (`DbDataSync.Scripting.Abstractions`) show Sync's `CanonicalType` to authors' scripts and
  `driver.csx` files. If the type goes, a forwarding alias with the same full name stays (SYNCAUTHORING-P15).
- **`ScriptTestService`** has its own small table of native names for the columns of a script test; it moves to the shared tables of the
  connection's engine.

## Open questions

The five proposals SYNCAUTHORING-P23 to SYNCAUTHORING-P27 (subject file).

## Defaults taken

Items 7 and 8 above. They become proposals if the owner has not answered when the branch is ready.
