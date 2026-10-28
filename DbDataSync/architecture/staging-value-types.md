# Staging value types: what the readers return, what staging does with it (A14a)

Sync stages a change set through the shared bulk writers (A14: `SqlServerBulkWriter`, A15: `PostgresBulkWriter`). The cells it hands them are
whatever .NET type the *source* driver's reader produced. `13b-owner-review.md` section 1 said nobody had listed those types; this is the list, and
what the two staging paths do with each of them **today** (before the conversion layer). The conversion layer (A14a) is held to this table: where it
changes what a value becomes, that is a behaviour change and goes in the release notes.

## How this was built, and what was run

| Part | Source | Run here? |
| --- | --- | --- |
| Reader types: SQL Server, PostgreSQL, MySQL, MariaDB, DuckDB | an integration test per engine creates a column of each type, stores a literal and reads it back with the driver's own provider (`ReaderTypesTests`) | **yes**, SQL Server 2022, PostgreSQL 17, MySQL 9, MariaDB 11, DuckDB (in memory). The result is the checked-in `ReaderTypes.<engine>.expected.tsv` next to each test and is the gate |
| Reader types: PostgreSQL logical slot (`wal2json`) | `PostgresValues.FromText` run on sample text (`LogicalSlotValueTypesTests`, no server) | yes |
| Reader types: Arrow Flight SQL | read from `ArrowValues.ValueAt` (`DbDataKit.Connections.FlightSql`) | no engine; the code is the specification |
| Reader types: JDBC | read from `JdbcDataReader.JdbcResultToClrObjectUnsafe` | no; needs IKVM and a JDBC jar |
| Reader types: Oracle | **Oracle's ODP.NET documentation, not executed** (no Oracle image on this machine). Rows marked "(docs)" are unverified | no |
| What staging does with each value | `StagingValueMatrixTests` (SQL Server and PostgreSQL): every loose value in `StagingMatrix.Values` into a column of every target type, through the real staging provider; the stored value read back, or the refusal. Checked in as `StagingMatrix.mssql.expected.tsv` and `StagingMatrix.postgres.expected.tsv` (about 50 values x 20 and 18 types each) | **yes**, same engines |

The two matrices are the gate: they fail when any cell changes, and `DBDATASYNC_MATRIX_UPDATE=1` regenerates them so the difference is reviewed. The
tables below are summaries of them; when they disagree, the files win.

The staging provider does not see the source column's declared type (the change set carries values and a column name, and a mapping's `Transform` can
change the type anyway), so every statement below is keyed by the **runtime type of the value**, which is what the writers see.

## 1. What each source reader returns, by canonical kind

`GetValue(i)` of the provider the driver reads with; `-` = the engine has no such type. Cells that are not what the column's name suggests are in bold.

| Canonical kind | SQL Server | PostgreSQL (query) | PostgreSQL (logical slot) | MySQL / MariaDB | DuckDB | Oracle (docs) | JDBC | Arrow Flight SQL |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Boolean | `bit` `bool` | `boolean` `bool` | `bool` (accepts `t`/`f`/`true`/`false`) | `boolean`, `tinyint(1)` `bool`; **`bit(n)` is `ulong`** | `bool` | `NUMBER(1)` is a number type | `BIT` `bool` | `bool` |
| Int8 | `tinyint` is `byte` | - | - | `tinyint` `sbyte`, unsigned `byte` | `sbyte` / `byte` | number | `getByte` (IKVM `sbyte`; the declared field type says `byte`) | `sbyte` / `byte` |
| Int16 | `short` | `short` | `short` | `short` / `ushort` | `short` / `ushort` | `NUMBER(p<5,0)` `short` (docs) | `short` | `short` / `ushort` |
| Int32 | `int` | `int`; `oid` is `uint` | `int` | `int`, `mediumint` `int`; unsigned `uint`; `year` `int` | `int` / `uint` | `NUMBER(p<10,0)` `int` (docs) | `int` | `int` / `uint` |
| Int64 | `long` | `long` | `long` | `long`; unsigned `ulong` | `long` / `ulong`; **`HUGEINT` and `UHUGEINT` are `BigInteger`** | `NUMBER(p<19,0)` `long` (docs) | `long` | `long` / `ulong` |
| Decimal | `decimal` (also `money`, `smallmoney`) | `decimal`; **unbounded `numeric` above 28 digits throws `OverflowException`, `NaN` throws `InvalidCastException`** at `GetValue` | `decimal` (same overflow) | `decimal` | `decimal` | `NUMBER` `decimal`; over 28 digits `OverflowException` (docs) | `decimal` through `decimal.Parse(BigDecimal.toString())` (overflows the same way) | `decimal`; **a value that does not parse as one is returned as `string`** |
| Float | `real` is `float` | `float` | `float` | `float` | `float` | `BINARY_FLOAT` `float` (docs) | `float` | `float` (half floats widened) |
| Double | `float` is `double` | `double` | `double` | `double` | `double` | `BINARY_DOUBLE` `double` (docs) | `double` | `double` |
| String | `string`; `char(n)` and `nchar(n)` come back **space padded** | `string`; `character(n)` padded; **`"char"` is `char`** | `string` | `string`; enum and set `string`; **`char(36)` is `Guid`** (a `char(36)` that is not a GUID throws) | `string`; **`BIT` is a `string` of 0 and 1** | `string`; `CLOB` `string` (docs) | `string` | `string` |
| Binary | `byte[]`; `rowversion` `byte[]` | `byte[]` | `byte[]` | `byte[]`; `geometry` is `byte[]` | **`BLOB` is a `Stream` (`UnmanagedMemoryStream`)** | `RAW`, `BLOB` `byte[]` (docs) | `byte[]` | `byte[]` |
| Date | **`DateTime`** at midnight | **`DateOnly`** (`infinity` becomes 9999-12-31) | `DateOnly` | `DateTime`; **a zero date `0000-00-00` throws `InvalidCastException`** | `DateOnly` | `DATE` is `DateTime` with its time (docs) | `DateOnly` | `DateTime` |
| Time | `TimeSpan` | **`TimeOnly`**; `timetz` is `DateTimeOffset` dated **0001-01-02** | **`TimeSpan`**; `timetz` is the text as `string` | `TimeSpan` (range +-838 hours; MySQL drops the fraction of a negative value in the sample, MariaDB keeps it) | `TimeOnly`; `TIMETZ` `DateTimeOffset` dated 0001-01-01 | - | `TimeOnly` | `TimeSpan` |
| Timestamp | `DateTime` (`datetime`, `datetime2`, `smalldatetime`; kind `Unspecified`) | `DateTime`, kind `Unspecified` | `DateTime` | `DateTime` (`datetime`, `timestamp`); a zero value throws | `DateTime` | `DateTime` (docs) | `DateTime`, kind `Unspecified`, through the machine's time zone | `DateTime`, `Unspecified` |
| Timestamp with time zone | `DateTimeOffset` with the stored offset | **`DateTime` of kind `Utc`** (converted to UTC; the offset is gone) | **`DateTimeOffset` with the offset of the text** | - | **`DateTime`**, UTC | `DateTimeOffset` or `DateTime` (docs, not settled) | falls to `getObject`: a Java object (not a .NET type) | `DateTimeOffset` |
| Guid | `Guid` | `Guid` | `Guid` | `char(36)` `Guid`; `binary(16)` is **`byte[]`** | `Guid` | `RAW(16)` `byte[]` (docs) | `getObject`: Java `UUID` or `string` | **`string`** |
| Json | `string` (`json` type) | `string`; **`jsonb` text is re-formatted** (`{"a": 1}`) | `string` | `string` (MySQL re-formats `{"a": 1}`, MariaDB keeps `{"a":1}`) | `string` | `string` (docs) | `string` | `string` |
| Xml | `string` | `string` | `string` | - | - | `XMLTYPE` (not settled) | `string` | - |
| Interval | - | `TimeSpan`; **an interval with months throws `InvalidCastException`** | **`string`** (`1 day 02:03:04`) | - | `TimeSpan`; **months throw `ArgumentOutOfRangeException`** | `INTERVAL DAY TO SECOND` `TimeSpan`; `YEAR TO MONTH` (docs, not settled) | - | - |
| List / array | - | **`int[]`, `string[]`, ...** | **`string`** (`{1,2,3}`) | - | **`List<T>`**; `STRUCT` and `MAP` are `Dictionary<,>` | - | - | **`string`** (`[1, 2]`) |
| Unmappable | **`geography`, `geometry`, `hierarchyid` throw `FileNotFoundException` at `GetValue`** (`Microsoft.SqlServer.Types` is not shipped) | `inet` `IPAddress`, `cidr` `IPNetwork`, `macaddr` `PhysicalAddress`, `bit(n)` `BitArray`, `point` `NpgsqlPoint`, ranges `NpgsqlRange<T>` | `string` | `geometry` `byte[]` | - | - | - | struct as `string` |

Also: scripted queries (`ScriptedQueryReader`) return whatever objects the script produces, so they have no table of their own; ODBC and
descriptor-defined drivers return the ODBC/JDBC types of their provider.

## 2. What staging does with each runtime type today

Rows are runtime types, columns target types. "stored" = the shared writer and the engine accept it; the stored form is the engine's own. "refused" =
the write fails (the whole batch). Full detail, one line per value and target type: the two `StagingMatrix.*.expected.tsv` files. The sample values are
chosen so that a value which *can* convert does (`int` 1, `double` 1.5, `"1"`).

### SQL Server (`SqlBulkCopy` through `SqlServerBulkWriter`; lenient: the provider converts)

| Target column type (canonical kind) | Stored from | Refused from | Notes |
| --- | --- | --- | --- |
| `bit` (Boolean) | every integer type, `float`, `double`, `decimal`, `bool`, the string `"true"` | `BigInteger`, `char`, other strings, binary, Guid, date and time types | any non-zero number is `true`, including 300, -1 and `NaN` |
| `tinyint` .. `bigint` (Int8..Int64) | `bool`, every integer type that fits, `float`/`double`/`decimal` (**rounded to the nearest, 1.5 becomes 2, 1.239 becomes 1**), `char` (**its code point: `'a'` becomes 97**), strings that parse as an integer (`"1"`) | out of range, `BigInteger`, `NaN`, infinity, other strings, binary, Guid, date and time types | `ulong.MaxValue` into `bigint` is refused |
| `decimal(p,s)`, `money` (Decimal) | integers, `float`, `double`, `decimal` (**rounded to the scale: 1.239 becomes 1.24**), strings that parse (`"1.5"`) | `BigInteger`, `NaN`, infinity, a value above the precision, `char`, other strings | |
| `real`, `float` (Float, Double) | numbers, strings that parse | `BigInteger`, `char`, other strings | `NaN` and infinity are refused by the server (`SqlException`) |
| `nvarchar(n)`, `varchar(n)` (String) | **every integer, float and decimal type (as text), `bool` (as `True`), `char`, strings, `DateTime` and `DateOnly` (as text)** | `BigInteger`, binary, Guid, `DateTimeOffset`, `TimeOnly`, `TimeSpan`; text longer than `n` | **`DateTime` is written as `03/17/2024 09:41:22`: client-side formatting, the fraction is gone** |
| `varbinary(n)` (Binary) | `byte[]` | everything else, including `Stream` | |
| `uniqueidentifier` (Guid) | `Guid` | everything else, including the string form | |
| `date` (Date) | `DateTime` (time dropped), `DateOnly`, **strings that parse as a date in the worker's culture (`"1.5"` becomes a date)** | numbers, `DateTimeOffset`, `TimeOnly`, `TimeSpan`, other strings | |
| `time(7)` (Time) | `TimeSpan` (under 24 hours), `TimeOnly`, the string `"09:41:22"` | everything else | |
| `datetime`, `datetime2` (Timestamp) | `DateTime`, `DateOnly`, strings that parse | numbers, `DateTimeOffset`, `TimeOnly`, `TimeSpan`; `datetime` also refuses a date before 1753 (`SqlTypeException`) | `DateTime` of kind `Utc` is stored as its wall clock, the kind is not looked at |
| `datetimeoffset` (Timestamp with time zone) | `DateTimeOffset`, `DateTime` (stored with offset +00:00, kind not looked at), `DateOnly`, strings | numbers, `TimeOnly`, `TimeSpan` | |
| `xml` (Xml) | strings (any text without NUL: a bare text node is valid XML content) | everything else | |

### PostgreSQL (binary `COPY` through `PostgresBulkWriter`; strict: the wire format has to match the column)

| Target column type (canonical kind) | Stored from | Refused from | Notes |
| --- | --- | --- | --- |
| `boolean` | `bool` | everything else | |
| `smallint`, `integer`, `bigint` | every integer type that fits, **and `float`, `double`, `decimal` (truncated: 1.5 becomes 1, where SQL Server rounds to 2)** | out of range, `bool`, `char`, strings, `BigInteger`, date and time types | |
| `numeric(p,s)` | integers (including `BigInteger`), `float`, `double`, `decimal` (rounded to the scale) | `bool`, `ulong.MaxValue`, `NaN`, infinity, above the precision, strings | |
| `real`, `double precision` | `float`, `double` (including `NaN` and infinity) | integers, `decimal`, strings | |
| `text`, `character varying(n)` | strings, `char`, `byte[]` (**its bytes as text**; `Stream` too) | numbers, bool, dates, Guid; text longer than `n` | |
| `bytea` | `byte[]`, `Stream` | everything else | |
| `uuid` | `Guid` | everything else, including the string form | |
| `date` | `DateTime` (time dropped, kind ignored), `DateOnly`, **and `int`/`ushort` as a number of days since 2000-01-01 (`1` becomes 2000-01-02, `300` becomes 2000-10-27)** | `DateTimeOffset`, strings | the integer case is a silent mis-read, not a conversion anyone asked for |
| `time` | `TimeOnly`, `TimeSpan`, **and `uint`/`long` as microseconds** | `DateTime`, strings, `TimeSpan` of a day or more | |
| `timestamp` | `DateTime` of kind `Unspecified`, **and `uint`/`long` as microseconds since 2000-01-01** | **`DateTime` of kind `Utc`**, `DateTimeOffset` | |
| `timestamptz` | **`DateTime` of kind `Utc` only**, and `uint`/`long` as microseconds | **`DateTime` of any other kind** (the usual `datetime2`, MySQL `datetime`, DuckDB `TIMESTAMP`), **`DateTimeOffset` unless its offset is zero** | cross-engine: a SQL Server or MySQL timestamp column cannot be staged into a PostgreSQL `timestamptz` column today |
| `interval`, `jsonb`, `timetz`, `money`, `inet`, arrays, enums, ... | nothing: `PostgresNpgsqlTypes` sends every type it does not list as `text`, and binary `COPY` of text into those columns is refused | everything | `json` takes text that is valid JSON. The refusal message names the alternative Kind, `PostgresDriverKinds.StagingTable`, which converts on the server |

## 3. The conversion layer (A14a): what it does with this

Decision on 2026-10-28 (13a): build a conversion layer between Sync's loose source types and the canonical batches the shared writers take. Strict
versus lenient (13b section 1) is **not answered**, so the layer keeps today's behaviour: the providers still convert as in section 2, and the layer adds
what section 2 shows is missing without changing an outcome.

`StagingValueGate` (Drivers.Generic) sits in the staging batch reader, one check per cell. For a column whose target type maps to a canonical kind it decides
once per column and runtime type, from `StagingValueRules` (generated from the two matrices):

* **Refuse**: a runtime type that no column of that canonical kind took in the matrix (every sample refused). The cell fails *before* the writer sees it,
  with one message that names the target table, the column, the column's declared type and the value's runtime type, never the value
  (`Staging into dbo.Orders: column 'Total' (decimal(18,2)) cannot take a value of type System.Guid from the source ...`). Before, the same failure arrived from the
  provider with whatever text it had: SqlClient usually names the column and the types; a PostgreSQL server error and a few client errors name neither.
* **Pass**: everything else, untouched: every cell section 2 shows as stored, including the lenient and the surprising ones (`1.5` into `tinyint` stored as
  2; PostgreSQL `int` into `date`), because changing them is the strict-versus-lenient decision; and every runtime type the matrix never measured (an `IPAddress`, a
  java object, a range), because the table says nothing about it and the provider decides, as before.
* PostgreSQL `interval` and `json`/`jsonb` targets are left to the provider on purpose: it refuses them with its own message, which carries the advice to stage through the
  other Kind. Unmappable target types and engines other than SQL Server and PostgreSQL pass everything.

**No value is converted.** The table is why: SQL Server accepts what PostgreSQL refuses (an `int` into `boolean`, a `DateTime` of the wrong kind into `timestamptz`) and the
reverse, so one canonical conversion cannot reproduce both engines, and a conversion per engine is exactly what the providers already do. Every change the layer makes
is in the refusal column: the matrices were regenerated with the gate in place and compared cell by cell with the ones measured before it: of 1,900 cells, **0 stored cells changed
and 0 refused cells became stored**; 1,074 refused cells (477 on SQL Server, 597 on PostgreSQL) now fail with `StagingValueException` instead of a provider exception, at the same
point of the load (the batch is not written either way).

What the layer could do beyond this is the findings below, each a behaviour change the owner has not asked for.

## 4. Findings (not changed here)

1. **PostgreSQL `COPY` mis-reads integers as dates, times and timestamps** (`int` into `date`, `uint`/`long` into `time`, `timestamp`, `timestamptz`): stored without error as a count of days or microseconds since 2000-01-01. A source integer column mapped onto a date column is a mapping mistake the engine does not catch.
2. **A PostgreSQL `timestamptz` target takes only `DateTime` of kind `Utc`** (deliberate, and the refusal explains it; but it is a cross-engine gap). SQL Server `datetime2`/`datetimeoffset`, MySQL `datetime` and DuckDB `TIMESTAMP` sources are refused. The logical-slot reader returns a `DateTimeOffset` with the offset of the decoded text, so it is refused whenever that offset is not zero.
3. **`DateTime` into a SQL Server `varchar`/`nvarchar` is formatted by the client** (`03/17/2024 09:41:22`, fraction dropped), and a string such as `"1.5"` is read as a date by `date`/`datetime`/`datetime2`/`datetimeoffset` targets. Both are the client's parsing and formatting, so they very likely follow the worker's culture; the matrix ran in one culture only and that is **not verified** under another.
4. **PostgreSQL `COPY` cannot stage `interval`, `jsonb`, `timetz`, `money`, arrays or any type missing from `PostgresNpgsqlTypes`.** The error says so and names the other staging Kind.
5. **Readers that cannot hand a value over at all**: SQL Server `geography`, `geometry`, `hierarchyid` (missing assembly); PostgreSQL unbounded `numeric` above 28 digits, `NaN`, intervals with months; MySQL zero dates and non-GUID `char(36)`; DuckDB intervals with months. These fail in the source driver before staging sees them.
6. **DuckDB `BLOB` is a `Stream`**, which SQL Server staging refuses and PostgreSQL staging accepts.
7. **Arrow Flight SQL**: a column type `ArrowValues.ValueAt` does not handle (fixed-size binary such as a UUID, durations, intervals, unions, maps) is returned as the placeholder string `<TypeName>`, which staging stores as text in a string column. A struct comes back as `{a: 1}` text.
8. `PgLogicalSlotReader` puts the raw value into its error text (`could not be read as <type>: '<value>'`); every other path here names the type only.

Questions that are the owner's: whether any of 1 to 4 should become a conversion or a refusal (strict), and whether a mapping may choose. Until then
nothing in this table changes.

## 5. Keeping it true

* A change to what a reader returns, or to what staging stores or refuses, fails `ReaderTypesTests`, `LogicalSlotValueTypesTests` or
  `StagingValueMatrixTests` until its `*.expected.tsv` is regenerated (`DBDATASYNC_MATRIX_UPDATE=1`) and the diff is read.
* New source driver: add its `ReaderTypesTests` and extend section 1.
* Run: SQL Server and PostgreSQL integration tests against the engines (`testenv/testenv.sh`), for example
  `dotnet test tests/DbDataSync.Drivers.MsSql.Tests --filter "FullyQualifiedName~StagingValueMatrixTests|FullyQualifiedName~ReaderTypesTests"`.
