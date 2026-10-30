# Staging value types: what the readers return, what staging does with it (A14a)

Sync stages a change set through the shared bulk writers (A14: `SqlServerBulkWriter`, A15: `PostgresBulkWriter`). The cells it hands them are
whatever .NET type the *source* driver's reader produced. `13b-owner-review.md` section 1 said nobody had listed those types; this is the list, and
what the two staging paths do with each of them. Sections 1 and 2 are the measurements from before the gate (A14a); section 3 says what the gate refuses now (A14a, A14b). Where the layer
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

(These tables describe what the *providers* do, as measured before the gate. Since A14b the gate refuses the cells of section 3a before the provider sees them; the checked-in matrices show the result.)

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

## 3. The conversion layer (A14a, A14b): what it does with this

Decision on 2026-10-28 (13a): build a layer between Sync's loose source types and the canonical batches the shared writers take. Decision on **2026-10-30**
(owner, 13c D1): **strict at the type level only, with no runtime impact.** The layer **refuses** the (runtime type, target kind) pairs listed below and in
section 3a, and **converts nothing**. Whether a particular value fits its column stays lenient, as it always was.

`StagingValueGate` (Drivers.Generic) sits in the staging batch reader, one check per cell. For a column whose target type maps to a canonical kind it decides
once per column and runtime type, from `StagingValueRules`:

* **Refuse, the engine would refuse anyway** (A14a, generated from the matrices): a runtime type that no column of that canonical kind took (every sample refused). The cell fails *before* the writer sees it,
  with one message that names the target table, the column, the column's declared type and the value's runtime type, never the value
  (`Staging into dbo.Orders: column 'Total' (decimal(18,2)) cannot take a value of type System.Guid from the source ...`). Before, the same failure arrived from the
  provider with whatever text it had: SqlClient usually names the column and the types; a PostgreSQL server error and a few client errors name neither.
* **Refuse, the engine would store something else** (A14b, section 3a): a runtime type the provider takes but stores as a different value, whatever the value: PostgreSQL `date` from `int`/`ushort` (days since 2000),
  `time`, `timestamp`, `timestamptz` from `uint`/`long` (microseconds since 2000), `text`/`varchar` from `byte[]`/`Stream` (bytes as characters); SQL Server integers from `char` (the code point) and, since the decision of 2026-10-30, `varchar`/`nvarchar` from `DateTime` and `DateOnly` (client-formatted text). The message is the same
  one, with the sentence that the engine "would store that type as a different value" in place of "refuses that type"; it carries no advice about the other Kind, whose behaviour for these pairs was not measured. The one pair with advice of its own is a date into SQL Server text: add a Transform to the mapped source column that formats the date or time as the text wanted.
  `StagingValueException.Reinterpreted` tells the two apart.
* **Pass**: everything else, untouched: every cell section 2 shows as stored that is not in the list above, including the lenient ones (`1.5` into `tinyint` stored as 2; a number out of range, text that must parse, a string longer than the column,
  `bool` as 1, a number into `bit`), because **value-level strictness is explicitly not wanted** (it would need a look at every value); and every runtime type the matrix never measured (an `IPAddress`, a java object, a range), because
  the table says nothing about it and the provider decides, as before.
* PostgreSQL `interval` and `json`/`jsonb` targets are left to the provider on purpose: it refuses them with its own message, which carries the advice to stage through the
  other Kind. Unmappable target types and engines other than SQL Server and PostgreSQL pass everything.

**No value is converted, parsed or range-checked.** SQL Server accepts what PostgreSQL refuses (an `int` into `boolean`, a `DateTime` of the wrong kind into `timestamptz`) and the
reverse, so one canonical conversion cannot reproduce both engines, and a conversion per engine is exactly what the providers already do. The cost per cell is unchanged: one type comparison against the column's last accepted type
(`Verdict` runs two short list scans once per column and runtime type, not per cell; see `benchmarks/staging-baseline.md`).

What changed in the matrices, cell by cell (1,900 cells, regenerated with `DBDATASYNC_MATRIX_UPDATE=1` and compared with the files of A14a):

| | A14a against the pre-gate matrices | A14b against A14a |
| --- | --- | --- |
| stored cells that changed | 0 | 30 stored became refused (16 PostgreSQL, 14 SQL Server): exactly the pairs of section 3a; of the SQL Server ones, 4 are the `char` cells and 10 the date-into-text cells (`DateTime` x 4 kinds and `DateOnly`, into `varchar(50)` and `nvarchar(50)`) |
| refused cells that became stored | 0 | 0 |
| refused cells whose exception changed | 1,074 (477 SQL Server, 597 PostgreSQL) now `StagingValueException` instead of a provider exception | 8 more: PostgreSQL `time` with `long:2^40`, `text` and `character varying(5)` with `byte[]:16`; SQL Server `nvarchar(5)` with the five `DateTime`/`DateOnly` values (the gate refuses by type, so a value the provider happened to refuse is refused by the gate) |
| every other cell | unchanged | unchanged |

## 3a. A14b: the refusals (strict at the type level)

Decision of the owner, 2026-10-30 (13c D1): **strict at the type level only, with no runtime impact.** The gate keeps its single check per cell (one type
comparison against the column's last accepted type); its refusal table grows. A (runtime type, target kind) pair is refused when the provider's current
lenient behaviour **reinterprets the value into something that is not the same logical value**. Whether a particular value fits (range, precision, length,
rounding, text that has to parse) stays lenient exactly as today: it needs per-value inspection, which is not wanted. No value is parsed, range-checked or
converted.

**The rule applied**, to every cell the two matrices show as `STORED`:

1. Refuse when the stored result has a different meaning from the source value *because of the type pair*, whatever the value: a count becomes a date, a
   letter becomes a number, bytes become characters.
2. Keep lenient: numeric to numeric (widening and narrowing, rounding and truncation: `double` 1.5 into `tinyint` stored as 2 is a value-fit question),
   text that the provider parses, number or boolean to text (the text form is the same value), date to a wider date type (`DateOnly` into `datetime2`),
   anything where the outcome depends on the value (range, length, precision, culture), and every type the matrices did not measure.
3. The gate decides per canonical kind, not per column type, so a pair is listed only when every column type of the kind that the matrices measured stores it
   the same way (each kind below has one measured column type per engine, except the strings, which have two on each engine).

### The refusals: stored before A14b, refused with `StagingValueException` since

| Engine | Target kind (measured column types) | Runtime types | What is stored today (matrix) | Why it is a reinterpretation |
| --- | --- | --- | --- | --- |
| PostgreSQL | Date (`date`) | `int`, `ushort` | `int` 1 stores 2000-01-02; `300` stores 2000-10-27; `-1` stores 1999-12-31 | a number is read as days since 2000-01-01 |
| PostgreSQL | Time (`time`) | `uint`, `long` | `1` stores 00:00:00.000001 | a number is read as microseconds |
| PostgreSQL | Timestamp (`timestamp`) | `uint`, `long` | `1` stores 2000-01-01 00:00:00.000001; `2^40` stores 2000-01-13 17:25:11.627776 | a number is read as microseconds since 2000-01-01 |
| PostgreSQL | Timestamp with time zone (`timestamptz`) | `uint`, `long` | same as `timestamp`, kind `Utc` | same |
| PostgreSQL | String (`text`, `character varying(n)`) | `byte[]`, `Stream` | `{1,2,3}` stores the characters U+0001 U+0002 U+0003 | bytes are re-read as UTF-8 characters (binary that is not valid UTF-8 is refused by the server today) |
| SQL Server | Int8, Int16, Int32, Int64 (`tinyint`, `smallint`, `int`, `bigint`) | `char` | `'a'` stores 97 | a letter becomes its code point |
| SQL Server (decided 2026-10-30) | String (`varchar(50)`, `nvarchar(50)`) | `DateTime` (every kind measured: `Unspecified`, `Utc`, midnight, year 1500), `DateOnly` | `DateTime` 2024-03-17 09:41:22 stores `03/17/2024 09:41:22` (fraction dropped); midnight stores `03/17/2024 00:00:00`; `DateOnly` stores `03/17/2024 00:00:00` (a time that was never there); year 1500 stores `01/01/1500 00:00:00` | `SqlBulkCopy` formats the date on the client: lossy, and the format follows the culture of the machine that runs Sync (the matrix ran in one culture, so the other cultures are inferred, not measured). The text is not the value the source held. Fix: add a **Transform** to the mapped source column that formats the date or time as the text wanted |

**The SQL Server date-into-text row was added after A14b's first commits** (owner decision, 2026-10-30, the same day: the borderline cell below was kept lenient and the owner refused it, because "a user could add a Transform to resolve the issue"). The message for exactly this pair adds the advice: "Add a Transform to the mapped source column that formats the date or time as the text you want" (it still never names the value). Which runtime types are in the rule, and why:

* `DateTime` and `DateOnly`: the matrix measured both stored into `varchar(50)` and `nvarchar(50)` as client-formatted text. Included.
* `DateTimeOffset`, `TimeOnly`, `TimeSpan` (and `TimeSpan` of three days): the matrix shows every one **refused** by SQL Server text columns already (the gate's `SqlServerNever` table), so none is stored as formatted text and none is added. Nothing to move.
* `char`, `nchar`, `text`, `ntext`: not measured (no such column in the matrix), so they keep passing as before; the rule is per canonical kind and the kind String has only the measured `varchar` and `nvarchar` columns. If the matrix gains them and they store the same text, add them here.

Cells: 30 (16 on PostgreSQL, 14 on SQL Server; the first 20 are A14b's first commit, the 10 date-into-text cells the decision of 2026-10-30) moved from `STORED` to `REFUSED StagingValueException`. The gate decides by runtime type, so eight
cells that the provider **already refuses** change only their exception type, to `StagingValueException` (PostgreSQL `time` with
`long:2^40`, `text` and `character varying(5)` with `byte[]:16`; SQL Server `nvarchar(5)` with the five date values): refused before and after, now with a message that names table, column and type. Nothing else is
expected to change; the diff of the regenerated matrices in the pull request shows it.

### Borderline cells kept lenient (the owner may flip any of them; the `varchar`/`nvarchar` from `DateTime`/`DateOnly` cell was flipped on 2026-10-30 and is in the table above)

| Engine and cell | Stored today | Why it stays lenient |
| --- | --- | --- |
| SQL Server `bit` from any number (`int` 300, `-1`, `double` `NaN`) | `true` | numeric 0/1 flags are the normal way a source holds a boolean, and what a non-0/1 number should mean is a value question |
| SQL Server integer, `decimal`, `money` from `bool` | 1 | `true` as 1 is a convention, the same logical value; PostgreSQL refuses it, SQL Server's provider converts |
| SQL Server `date`, `datetime`, `datetime2`, `datetimeoffset` from strings (`"1.5"` stores 2026-01-05) | a date | text that must parse is value-level by the decision |
| SQL Server `datetimeoffset` from `DateTime` | offset +00:00 whatever the kind | the wall clock is kept; assigning an offset is the provider's convention and depends on the value's kind |
| SQL Server `datetime`, `datetime2` from `DateOnly`; `date` from `DateTime` | midnight; time dropped | widening and truncation of the same date or instant, a precision question |
| both engines, integers from `float`, `double`, `decimal` | rounded (SQL Server) or truncated (PostgreSQL) | numeric narrowing, a value-fit question |
| PostgreSQL `time` from `TimeSpan` | stored under 24 hours | value range |
| PostgreSQL `numeric` from integers and `BigInteger` | exact | widening |
| both engines, strings into numeric, boolean, `xml`, `time`, `uniqueidentifier` where stored | parsed | text that parses is lenient by the decision |

### Not measured: left passing, as today

The matrices have no value of: `Int128`/`UInt128`, `Half`, `IPAddress`, `NpgsqlRange<T>`, `BitArray`, `Dictionary<,>`; the only `DateTimeOffset` has offset +02:00 and the
only `DateTime` kinds are `Unspecified` and `Utc`. No column type was measured for: SQL Server `smalldatetime`, `smallmoney`, `char`, `nchar`, `text`, `ntext`,
`binary`, `rowversion`, `sql_variant`, `varchar(max)`; PostgreSQL `char(n)`, `citext`, `timetz`, `money`, enums, domains, arrays, `numeric` without precision.
Engines other than SQL Server and PostgreSQL do not use the gate. All of these keep passing to the provider.

## 4. Findings (finding 1 is resolved by A14b; the others are not changed)

1. **Resolved by A14b (2026-10-30): PostgreSQL `COPY` mis-read integers as dates, times and timestamps** (`int` into `date`, `uint`/`long` into `time`, `timestamp`, `timestamptz`): stored without error as a count of days or microseconds since 2000-01-01. Now refused by type, with a message naming table, column and type.
2. **A PostgreSQL `timestamptz` target takes only `DateTime` of kind `Utc`** (deliberate, and the refusal explains it; but it is a cross-engine gap). SQL Server `datetime2`/`datetimeoffset`, MySQL `datetime` and DuckDB `TIMESTAMP` sources are refused. The logical-slot reader returns a `DateTimeOffset` with the offset of the decoded text, so it is refused whenever that offset is not zero.
3. **Resolved for the first half by A14b (2026-10-30): `DateTime`/`DateOnly` into a SQL Server `varchar`/`nvarchar` was formatted by the client** (`03/17/2024 09:41:22`, fraction dropped) and is now refused, with the advice to add a Transform that formats the date. A string such as `"1.5"` is still read as a date by `date`/`datetime`/`datetime2`/`datetimeoffset` targets (text that parses, lenient by decision), which very likely follows the worker's culture; the matrix ran in one culture only and that is **not verified** under another.
4. **PostgreSQL `COPY` cannot stage `interval`, `jsonb`, `timetz`, `money`, arrays or any type missing from `PostgresNpgsqlTypes`.** The error says so and names the other staging Kind.
5. **Readers that cannot hand a value over at all**: SQL Server `geography`, `geometry`, `hierarchyid` (missing assembly); PostgreSQL unbounded `numeric` above 28 digits, `NaN`, intervals with months; MySQL zero dates and non-GUID `char(36)`; DuckDB intervals with months. These fail in the source driver before staging sees them.
6. **DuckDB `BLOB` is a `Stream`**, which SQL Server staging refuses and PostgreSQL staging accepts.
7. **Arrow Flight SQL**: a column type `ArrowValues.ValueAt` does not handle (fixed-size binary such as a UUID, durations, intervals, unions, maps) is returned as the placeholder string `<TypeName>`, which staging stores as text in a string column. A struct comes back as `{a: 1}` text.
8. `PgLogicalSlotReader` puts the raw value into its error text (`could not be read as <type>: '<value>'`); every other path here names the type only.

Open for the owner: whether any of 2 to 4 should become a conversion or a refusal, and the borderline cells of section 3a. Strictness at the value level is decided against.

## 5. Keeping it true

* A change to what a reader returns, or to what staging stores or refuses, fails `ReaderTypesTests`, `LogicalSlotValueTypesTests` or
  `StagingValueMatrixTests` until its `*.expected.tsv` is regenerated (`DBDATASYNC_MATRIX_UPDATE=1`) and the diff is read.
* New source driver: add its `ReaderTypesTests` and extend section 1.
* Run: SQL Server and PostgreSQL integration tests against the engines (`testenv/testenv.sh`), for example
  `dotnet test tests/DbDataSync.Drivers.MsSql.Tests --filter "FullyQualifiedName~StagingValueMatrixTests|FullyQualifiedName~ReaderTypesTests"`.
