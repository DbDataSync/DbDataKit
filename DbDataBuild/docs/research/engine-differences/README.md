# Engine differences: what the probes found

`tests/DbDataBuild.Tests.Conformance/EngineDifferenceProbes.cs` runs about 200 scalar expressions and aggregates the way the tool would send them (lowered from DuckDB's plan, rewritten by the target rules,
transpiled) on SQL Server 2022 and PostgreSQL 17, over seeded rows built for the edges (negatives, zero, NULL, a half, an emoji, accents, empty and padded text, the largest INT), and compares each answer with
DuckDB's. `EngineProbe` does the three-way run; the text column of the probe table has the collation the string profile usually names (`Latin1_General_100_BIN2`, `"C"`), so the comparisons are the tool's, not a
database default's. A case whose answer is DuckDB's is a regression test. A difference is allowed only if it is in `Known` there, with its reason; one that disappears must be taken out. Run with
`DDB_PROBE_OUT=/some/file` for the full report (every result, and the SQL that was sent).

## Found, and fixed (entry 40)

| Case | What went wrong | Fix |
|---|---|---|
| any query with `''` (an empty string) | the plan is serialized with `skip_empty`, which drops the value of an empty string; `render` crashed with an unhandled exception | the lowerer reads it as `''`; any other unexpected shape is now a refusal (DDB-324), never a crash |
| `string_agg(x, sep ORDER BY ...)` | the separator and the ordering live outside the arguments in the plan and were dropped: `string_agg(x, ' \| ')` came out as `string_agg(x)` (the wrong separator) | the separator and ORDER BY are kept; any other aggregate with data outside its arguments (`median`, `quantile_cont`, `first(x ORDER BY ..)`) is refused |
| `position(a IN b)` | arguments swapped on both engines (searched `b` in `a`) | lowered as `strpos(b, a)` |
| `CAST(decimal AS INTEGER)` | DuckDB rounds half away from zero, SQL Server truncates | `CAST(round(x, 0) AS INTEGER)` (rule `decimal-to-int`) |
| `CAST(double AS INTEGER)` | DuckDB rounds half to even, SQL Server truncates (PostgreSQL agrees with DuckDB) | target rule `double-to-int` on SQL Server |
| `CAST(double AS DECIMAL(p, s))` | DuckDB scales by 10^s and rounds half away from zero (`0.285` is `0.28`, `819.025` is `819.03`); SQL Server converts the exact binary value (`819.02`), PostgreSQL the shortest text (`0.29`). Found by the template conformance test | lowered as `CAST(round(CAST(x AS DOUBLE), s) AS DECIMAL(p, s))` (rule `double-to-decimal`), the shape the `round-double` rule rewrites |
| `A UNION B UNION C` | DuckDB flattens it into one operator with three children and the lowerer read two: the third query was silently dropped (a calendar missing days). Found by the template conformance test | every child is joined in order |
| `JOIN ... ON a.k = b.k AND b.f IS NOT NULL` | the binder moves the one-sided predicate into a filter on that input, and the join read the input's table without its filters: the predicate vanished (rows with a NULL `f` joined). Found by the template conformance test | an input with filters is read as WHERE (inner join, or the side a left or right join keeps), as part of ON (the side that is filled in with NULLs), or a derived table (a full join, or an input with a limit, DISTINCT or window) |
| `lpad(s, n, p)`, `rpad` | SQL Server has neither | target rule `pad-to-length` for a literal length up to 4000 and a literal pad (chinook) |
| `split_part(s, sep, n)` | the plan's `array_extract(string_split(...))` is on neither engine | rule `split-part` on PostgreSQL; refused on SQL Server (matrix `fn.split_part`) |
| `string_agg(x, sep ORDER BY ...)` on PostgreSQL | polyglot writes `LISTAGG` | rule `string-agg-array` (`array_to_string(array_agg(...))`) |
| `date + 3`, `date - n` | SQL Server: operand type clash with an integer | lowering marks it, rule `date-plus-days` writes an interval of days on SQL Server |
| `strpos(a \|\| b, x)`, `position`, `starts_with` | the transpile leaves `\|\|` inside the arguments of a function it rewrites into another shape (`CHARINDEX('x', a \|\| b)`) | rule `concat-plus` writes every `\|\|` as `+` on SQL Server |
| `WITH RECURSIVE` | not lowered | lowered; both parts cast to one text and decimal type; SQL Server limits in the matrix row `syntax.recursive_cte` |
| `count(*) OVER (...)` | written `count() OVER ()` | written `count(*)` |
| `date_diff('year' \| 'month' \| 'quarter')` on PostgreSQL | AGE() counts whole elapsed periods, DuckDB boundaries | rule `date-diff-boundaries` |
| `date_diff('week')` | SQL Server counts boundaries from DATEFIRST, PostgreSQL's cast rounds, DuckDB counts whole seven-day periods toward zero | rule `date-diff-weeks` on both |
| a product of decimals wider than 38 digits | DuckDB multiplies exactly; SQL Server rounds each product to scale 6 | cataloged: matrix row `type.decimal_product_wide`, probe `dec_product_wide` |
| `x % 0` | NULL in DuckDB, an error on both engines | `x % NULLIF(0 as written, 0)` unless the divisor is a non-zero constant |
| `//` | polyglot cannot read it | integers only, as truncating division with NULL on zero; other types refused |
| `round(x)` | no one-argument form on SQL Server | `round(x, 0)` |
| `substr(s, n)` | SQL Server's SUBSTRING needs a length | a length of 2147483647 is added |
| `year()`, `month()`, `day()`, `hour()`, `minute()`, `second()`, `quarter()`, `dayofweek()`, `dayofyear()`, `isodow()` | not translated for PostgreSQL (and most not for SQL Server) | lowered as `date_part(...)`, which both translate |
| `date_part('dow')`, `isodow` | `DATEPART(weekday)` depends on `@@DATEFIRST` | target rule `weekday-datefirst`: count days from 1900-01-07 (a Sunday) or 1900-01-01 |
| `date - date` | DuckDB gives the days; SQL Server cannot subtract dates | `date_diff('day', b, a)` |

## Found, and written down (support matrix rows, or `Known` in the probe)

- **Division by zero** (`op.div`): DuckDB 1.5 gives Infinity and NaN, the engines NULL.
- **Characters outside the BMP on SQL Server** (`fn.length` note, DESIGN 7.4): `LEN`, `SUBSTRING`, `LEFT`, `RIGHT`, `REVERSE` work on UTF-16 code units, so an emoji is two and can be cut in half. PostgreSQL is right.
- **Case mapping** (`str.case_mapping`): SQL Server leaves `ß`; PostgreSQL under the C collation changes ASCII only.
- **Missing functions** (rows `fn.pad`, `fn.contains`, `fn.mode`, `fn.corr`; `DDB-305` for the rest such as `week`, `last_day`, `split_part`): the engine errors, loudly.
- **`string_agg` on PostgreSQL** (`fn.string_agg`): polyglot writes `LISTAGG`; unsupported until fixed upstream. SQL Server is right.
- **`date_part('week'|'epoch')`** (`fn.date_part_calendar`), **negative `substr` start** (`fn.substring_negative`), **`date_diff('month'|'year')` on PostgreSQL** (`fn.date_diff`): approximated or unsupported, with notes.
- **Text forms**: `CAST(double AS VARCHAR)` differs everywhere (1e+010, 10000000000, 10000000000.0), `CAST(datetime2 AS VARCHAR)` keeps six fractional digits on SQL Server; cast to DECIMAL or format explicitly.
- **Trailing spaces in comparisons** on SQL Server: the string profile (`trailing_space`), as before.
- **Errors where DuckDB returns a value**: `exp()` overflow, `CAST('1.5' AS INTEGER)`, a negative length to `substr`, a negative count to `left`. Loud, not wrong.
