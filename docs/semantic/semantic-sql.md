# Semantic SQL

**Status: draft for the owner's approval (plan 19, J17).** Implemented and tested: `DbDataKit.Semantic.Sql` (`SemanticSqlRenderer`, `SemanticSqlTranslator`, `RequestNormalizer`); every query fixture has its SQL form checked in (`DbDataKit/tests/DbDataKit.Semantic.Tests/golden/semantic-sql/`) and reads back to its request. Plan 14 R6 is the design this follows; section 6 lists what it settles beyond R6 and what it leaves for later.

Semantic SQL is **a subset of DuckDB SQL, translated to and from the JSON semantic request** ([query.md](query.md)). There is no grammar of our own: DuckDB's parser (`json_serialize_sql`, the one the sandbox already uses) reads the text, and every accepted query is exactly one request. Anything outside the subset is refused with a named code, never approximated. Agents may write either form; people mostly read SQL. Nothing here is executed by DuckDB as written: the engine runs the request.

```sql
SELECT customers.region,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       MEASURE(orders.net_revenue) FILTER (WHERE orders.status = 'Completed') AS completed
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1' AND customers.region <> 'North'
GROUP BY ALL
HAVING MEASURE(orders.net_revenue) > 100
ORDER BY MEASURE(orders.net_revenue) DESC
LIMIT 100
```

## 1. Names

- **`FROM` names the semantic model** (the catalog's `name`). It is not a table; the model's members are its columns. A text that names another model is `sql_wrong_model`.
- **A member is written as the request writes it**: `dimension.attribute` (`customers.region`) for an attribute, `fact.measure` (`orders.net_revenue`) for a measure, so a name is the same in JSON, in SQL and in the result. Each part is an identifier; a part that is not a plain lower-case word, or that DuckDB reserves (`window`, `offset`, `select`), is written in double quotes. Quoting never changes the name.
- **An alias** (`AS rev`) names a measure entry; a single word with no dot is an alias, a dotted name is a member. An attribute cannot be renamed.

## 2. The mapping

| Semantic SQL | Request |
| --- | --- |
| `FROM model` | the catalog; `FROM model(timezone := 'UTC', segments := ['big'])`: the options (section 4) |
| an attribute in the SELECT list (`customers.region`) | `attributes`, in order |
| `MEASURE(orders.net_revenue)` | a measure name in `measures` |
| `MEASURE(m, spread := 'blank', rollup := {'date.fiscal_month': 'avg'}, empty := 'zero', acknowledge := TRUE) AS x` | an override entry: `measure`, the named arguments by their key, `as` |
| `MEASURE(m) FILTER (WHERE ...) AS x` | an override entry with `filters` |
| `rank(MEASURE(m), within := ['customers.region'], keep := 3) AS r` | a transform entry: the function name is `transform`, its first argument is `measure` (`MEASURE(m)`, or the bare alias of an earlier entry), the named arguments are the other keys, `AS` is `as`. Every transform the engine has is written this way (`pct_of_total`, `running_total`, `window`, `index`, `prior_period`, `per_entity`, `share_of_parent`, `rank`) |
| `avg(orders.unit_price) AS x`, `count(DISTINCT c) AS x` | an aggregate of a column (`agg`, `of`, `as`; `count_distinct` is `count(DISTINCT ...)`) |
| `WHERE` | `filters`; `AND` joins top-level entries, `OR` is an `or` filter, a parenthesized `AND` inside `OR` is an `and` |
| `a = v`, `a IN (v, w)` | `equals` with one value, with several |
| `a <> v`, `a NOT IN (v, w)` | `not_equals` |
| `a > v`, `>=`, `<`, `<=` | `gt`, `gte`, `lt`, `lte` |
| `a BETWEEN v AND w` | `between` |
| `contains(a, v, w)`, `starts_with(a, v, w)`, `NOT contains(a, v, w)` | `contains`, `starts_with`, `not_contains` with the values (any of, for the first two; none of, for the last). These are the engine's functions, not DuckDB's: they take any number of values |
| `a IS NOT NULL`, `a IS NULL` | `set`, `not_set` |
| `last_complete(date.fiscal_month, 3)`, `current(a)`, `period_to_date(a)` | the relative period operators (`count` for `last_complete`) |
| `operator(a, 'name', v, w, count := n)` | an operator written by name, for one with no form of its own (an unknown operator, or a known one with the wrong number of values: the engine then refuses it with its own code, as it does the JSON) |
| `HAVING MEASURE(m) > v` | a `filters` entry with `measure` (it follows the `WHERE` entries) |
| `QUALIFY alias > v` | `post_filters`: conditions on a transform's output |
| `GROUP BY ALL`, or the attributes listed | no `totals`. The list must be the SELECT list's attributes (`sql_group_by_mismatch`) |
| `GROUP BY ROLLUP (a, b)` | `totals: {subtotal_by: [a, b], grand_total: true}`; attributes not inside the ROLLUP partition the rows (`GROUP BY m, ROLLUP (a, b)`) |
| `GROUP BY CUBE (a, b)` | the same with `cube: true` |
| `GROUP BY GROUPING SETS ((m, a, b), (m, a))` | `grand_total: false`: the chain's prefixes (or every non-empty subset) without the empty set |
| `ORDER BY a, MEASURE(m) DESC, alias` | `order`; `ASC` is the default |
| `LIMIT n` | `limit` (the request requires one) |

Values are a string, a number, `TRUE`, `FALSE`, `NULL`, a list `[a, b]` or an object `{'key': v}`; `DATE '2026-01-01'` is the string. Named arguments are written with `:=`.

## 3. Same request

SQL cannot tell some requests apart that JSON can, so two requests that differ only in these are **the same request** (`RequestNormalizer`), and the round trip request, SQL, request returns the normalized request: nested `and` entries are one flat list; conditions on measures come after conditions on attributes; a measure entry with only `measure` is the measure name; numbers are decimals; `grand_total` is explicit (false) and `cube: false` is absent; an order entry names its direction; an empty list (`attributes`, `filters`, `values`...) is the same as none.

## 4. Options

Everything the request has that is not a clause goes in the model's argument list, with the key as the name and the value as a literal: `timezone`, `segments`, `assumptions`, any `x_` key, and `totals := {...}` for totals that GROUP BY cannot say (a `compute` hint, or one listed attribute with no grand total, whose grouping sets are the same as without totals). The renderer writes them, the translator reads them back, so no key of a request is lost.

## 5. Refusals

| Code | When |
| --- | --- |
| `sql_syntax` | DuckDB's parser refuses the text, or it is not one statement |
| `sql_unsupported` | `*`, `DISTINCT`, `OFFSET`, `WITH`, joins, subqueries and set operations in `FROM`, `UNION`, window functions, `ORDER BY` a position or an expression, `NULLS FIRST/LAST`, `LIKE` and other functions in a condition, arithmetic on measures, a cast that is not a date, a value that is not a literal. The message names the construct and what to write instead |
| `sql_group_by_mismatch` | the GROUP BY is not the SELECT list's attributes, or grouping sets that are not a ROLLUP, a CUBE or a prefix chain |
| `sql_wrong_model` | `FROM` names another model |

A request that parses but is invalid (an unknown member, an unknown operator, a measure filter inside an `or`) is the engine's to refuse, with the codes in [core.md](core.md) and [catalog.md](catalog.md); the translator checks syntax and shape, not the catalog. The message of an error is first the line, `$` is its path.

## 6. What this settles, and what it leaves

Beyond plan 14 R6, for approval:

1. **Member names stay `dimension.attribute` and `fact.measure`**, as in the catalog and every result, rather than nested structures with the conformed dimensions at the top. The nested form (`orders.customer.region` as the explicit path from a fact, `customer.*`, `GROUP BY customer`) needs a model with conformed dimensions and path resolution that the catalog does not have yet, and is not needed for any fixture; it can be added without changing what is here.
2. **Transforms are written as functions with named arguments**, one rule for all of them, rather than window-function patterns (`MEASURE(m) / SUM(MEASURE(m)) OVER (...)`). That keeps every transform, including `window`, `index` and `per_entity`, expressible and lossless. The window patterns can be accepted as aliases of the functions later.
3. **`contains`, `starts_with`, `last_complete`, `current`, `period_to_date`, `operator` and `MEASURE` are the language's own functions**, parsed by DuckDB as ordinary calls and never run by it. `LIKE` is refused: its escapes differ between engines and the engine's `contains` does not need them.
4. **Options in the `FROM` argument list**, not in comments or a second statement, so the text is one SQL query that any SQL parser reads.
5. **Not here yet:** `AGGREGATE(m)` as a synonym of `MEASURE(m)`; `AT (...)` modifiers; the outer query over a semantic query's result (a derived table, run in the sandbox as a completion); flattened names with `flat_name`; `GROUP BY customer` for a whole dimension. Each is additive.
6. **The text a person writes may be freer than the canonical text** (any case, any order of clauses DuckDB accepts); the canonical text is what `SemanticSqlRenderer` writes, and is what explain views and run records show.

## 7. Where it is tested

`SemanticSqlTests`: for each of the query fixtures, the canonical SQL equals the checked-in file, the SQL reads back to the fixture's normalized request, and the engine gives the fixture's expected answer to the request that came back; plus the refusals above, a hand-written query in a different style, and reserved-word quoting. Change a request, a fixture or the renderer, and `UPDATE_GOLDEN=1` rewrites the SQL forms: read the diff.
