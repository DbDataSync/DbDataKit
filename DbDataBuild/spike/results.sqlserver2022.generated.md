polyglot-sql 0.13.1 @ 0a7a1a7
SQL Server 16.0.4265.3 / SQL_Latin1_General_CP1_CI_AS
PostgreSQL 17.11 / en_US.utf8
fabric* = Fabric-transpiled text executed on SQL Server as a proxy (no Fabric engine here).

| id | expect | sqlserver | fabric* | postgres |
|---|---|---|---|---|
| int_division | differs | MATCH | MISMATCH | MATCH |
| int_division_cols | differs | MISMATCH | EXEC_ERR | MISMATCH |
| cast_varchar_nolen | differs | MATCH | MATCH | MATCH |
| cast_varchar_len | translate | MATCH | MATCH | MATCH |
| unicode_literal | differs | MATCH | MATCH | MATCH |
| concat_pipes | differs | MATCH | MATCH | MATCH |
| concat_null | differs | MATCH | MATCH | MATCH |
| length_fn | differs | MISMATCH | MISMATCH | MATCH |
| substr_fn | translate | MATCH | MATCH | MATCH |
| lower_upper | translate | MATCH | MATCH | MATCH |
| coalesce | translate | MATCH | MATCH | MATCH |
| ifnull | translate | MATCH | MATCH | MATCH |
| like_case | differs | MISMATCH | MISMATCH | MATCH |
| ilike | differs | MATCH | MATCH | MATCH |
| regexp | unsupported | SYNTAX_ERR | SYNTAX_ERR | MATCH |
| limit | translate | MATCH | MATCH | MATCH |
| limit_offset | translate | MATCH | MATCH | MATCH |
| limit_no_order | translate | MATCH | MATCH | MATCH |
| qualify | differs | MATCH | SYNTAX_ERR | MATCH |
| date_trunc_month | translate | MISMATCH | MISMATCH | MATCH |
| date_trunc_day | translate | MATCH | MATCH | MATCH |
| date_add_interval | translate | MISMATCH | MISMATCH | MATCH |
| date_diff | translate | MATCH | MATCH | MATCH |
| extract_year | translate | MATCH | MATCH | MATCH |
| current_ts | differs | MATCH | MATCH | MATCH |
| current_date_fn | differs | MATCH | MATCH | MATCH |
| group_by_ordinal | translate | EXEC_ERR | EXEC_ERR | MATCH |
| group_by_all | unsupported | SYNTAX_ERR | SYNTAX_ERR | EXEC_ERR |
| distinct | translate | MATCH | MATCH | MATCH |
| union_dedupe | translate | MATCH | MATCH | MATCH |
| union_all | translate | MATCH | MATCH | MATCH |
| row_number | translate | MATCH | MATCH | MATCH |
| window_sum | translate | MATCH | MATCH | MATCH |
| lag_lead | translate | MATCH | MATCH | MATCH |
| nulls_last | differs | MATCH | MATCH | MATCH |
| bool_predicate | differs | MATCH | MATCH | MATCH |
| bool_literal | differs | MATCH | MATCH | MATCH |
| decimal_mult | differs | MATCH | MATCH | MATCH |
| cte | translate | MATCH | MATCH | MATCH |
| left_join | translate | MATCH | MATCH | MATCH |
| case_when | translate | MATCH | MATCH | MATCH |
| in_list | translate | MATCH | MATCH | MATCH |
| list_literal | unsupported | EXEC_ERR | EXEC_ERR | MATCH |
| struct_literal | unsupported | SYNTAX_ERR | SYNTAX_ERR | EXEC_ERR |
| unnest | unsupported | EXEC_ERR | EXEC_ERR | MATCH |
| int_overflow | differs | BOTH_ERR | BOTH_ERR | BOTH_ERR |
| try_cast | translate | MISMATCH | MISMATCH | EXEC_ERR |
| quoted_ident | translate | MATCH | MATCH | MATCH |
| bool_and_or_not | translate | MATCH | MATCH | MATCH |
| str_eq_profile | differs | MISMATCH | MISMATCH | MATCH |
| str_in_profile | differs | MISMATCH | MISMATCH | MATCH |
| compare_ops | translate | MATCH | MATCH | MATCH |
| arithmetic_ops | translate | MATCH | MATCH | MATCH |
| unary_minus | translate | MATCH | MATCH | MATCH |
| aggregates | translate | MATCH | MATCH | MATCH |
| avg_int | differs | MISMATCH | MISMATCH | MATCH |
| avg_decimal | translate | MISMATCH | MISMATCH | MATCH |
| left_join_using | differs | SYNTAX_ERR | SYNTAX_ERR | MISMATCH |
| natural_join | unsupported | SYNTAX_ERR | SYNTAX_ERR | MATCH |
| cte_multi | translate | MATCH | MATCH | MATCH |
| select_distinct_on | unsupported | MATCH | MATCH | MISMATCH |
| sample_clause | unsupported | SYNTAX_ERR | SYNTAX_ERR | EXEC_ERR |
| lateral_join | unsupported | MATCH | MATCH | MATCH |
| group_having | translate | MATCH | MATCH | MATCH |
| between | translate | MATCH | MATCH | MATCH |
| is_null | translate | MATCH | MATCH | MATCH |
| not_in | translate | MATCH | MATCH | MATCH |
| inner_join | translate | MATCH | MATCH | MATCH |
| subquery_from | translate | MATCH | MATCH | MATCH |
| exists_subquery | translate | MATCH | MATCH | MATCH |
| in_subquery | translate | MATCH | MATCH | MATCH |
| intersect_except | translate | MATCH | MATCH | MATCH |
| except | translate | MATCH | MATCH | MATCH |
| nullif_fn | translate | MATCH | MATCH | MATCH |
| abs_round | differs | MATCH | MATCH | EXEC_ERR |
| trim_fns | translate | MATCH | MATCH | MATCH |
| replace_fn | translate | MISMATCH | MISMATCH | MATCH |
| case_simple | translate | MATCH | MATCH | MATCH |
| full_outer_join | translate | MATCH | MATCH | MATCH |
| cross_join | translate | MATCH | MATCH | MATCH |
| date_literal_cast | translate | MATCH | MATCH | MATCH |
| max_by_cast_int | differs | BOTH_ERR | BOTH_ERR | BOTH_ERR |
| count_over_window | translate | MATCH | MATCH | MATCH |
| cast_int | translate | MATCH | MATCH | MATCH |
| cast_bigint | translate | MATCH | MATCH | MATCH |
| cast_smallint | translate | MATCH | MATCH | MATCH |
| cast_double | translate | MATCH | MATCH | MATCH |
| cast_timestamp | translate | MATCH | MATCH | MATCH |
| cast_boolean | differs | MATCH | MATCH | MATCH |
| cast_ts_to_date | translate | MATCH | MATCH | MATCH |
| abs_fn | translate | MATCH | MATCH | MATCH |
| round_decimal | translate | MATCH | MATCH | MATCH |
| floor_ceil | translate | MATCH | MATCH | MATCH |
| right_join | translate | MATCH | MATCH | MATCH |

## Non-matching details

- **int_division** / fabric: MISMATCH: duckdb=[3.5] target=[3] (sorted row 0)
  - sql: `SELECT 7 / 2 AS x`
- **int_division_cols** / sqlserver: MISMATCH: duckdb=[Infinity] target=[∅] (sorted row 5)
  - sql: `SELECT CAST(a AS FLOAT) / NULLIF(b, 0) AS x FROM t`
- **int_division_cols** / fabric: EXEC_ERR: Divide by zero error encountered.
  - sql: `SELECT a / b AS x FROM t`
- **int_division_cols** / postgres: MISMATCH: duckdb=[Infinity] target=[∅] (sorted row 5)
  - sql: `SELECT CAST(a AS DOUBLE PRECISION) / NULLIF(b, 0) AS x FROM t`
- **length_fn** / sqlserver: MISMATCH: duckdb=[4] target=[3] (sorted row 4)
  - sql: `SELECT LEN(s) AS x FROM t`
- **length_fn** / fabric: MISMATCH: duckdb=[4] target=[3] (sorted row 4)
  - sql: `SELECT LEN(s) AS x FROM t`
- **like_case** / sqlserver: MISMATCH: row count duckdb=2 target=4
  - sql: `SELECT * FROM t WHERE s LIKE 'abc%'`
- **like_case** / fabric: MISMATCH: row count duckdb=2 target=4
  - sql: `SELECT * FROM t WHERE s LIKE 'abc%'`
- **regexp** / sqlserver: SYNTAX_ERR: 'REGEXP_LIKE' is not a recognized built-in function name.
  - sql: `SELECT * FROM t WHERE REGEXP_LIKE(s, '^a.*z$')`
- **regexp** / fabric: SYNTAX_ERR: 'REGEXP_LIKE' is not a recognized built-in function name.
  - sql: `SELECT * FROM t WHERE REGEXP_LIKE(s, '^a.*z$')`
- **qualify** / fabric: SYNTAX_ERR: Incorrect syntax near 'ROW_NUMBER'.
  - sql: `SELECT a, b FROM t QUALIFY ROW_NUMBER() OVER (PARTITION BY a ORDER BY CASE WHEN b IS NULL THEN 1 ELSE 0 END, b) = 1`
- **date_trunc_month** / sqlserver: MISMATCH: duckdb=[2023-12-01 00:00:00] target=[2023-12-01] (sorted row 0)
  - sql: `SELECT DATETRUNC(MONTH, d) AS x FROM t`
- **date_trunc_month** / fabric: MISMATCH: duckdb=[2023-12-01 00:00:00] target=[2023-12-01] (sorted row 0)
  - sql: `SELECT DATETRUNC(MONTH, d) AS x FROM t`
- **date_add_interval** / sqlserver: MISMATCH: duckdb=[2024-01-03 00:00:00] target=[2024-01-03] (sorted row 0)
  - sql: `SELECT DATEADD(DAY, 3, d) AS x FROM t`
- **date_add_interval** / fabric: MISMATCH: duckdb=[2024-01-03 00:00:00] target=[2024-01-03] (sorted row 0)
  - sql: `SELECT DATEADD(DAY, 3, d) AS x FROM t`
- **group_by_ordinal** / sqlserver: EXEC_ERR: Each GROUP BY expression must contain at least one column that is not an outer reference.
  - sql: `SELECT a, COUNT_BIG(*) FROM t GROUP BY 1`
- **group_by_ordinal** / fabric: EXEC_ERR: Each GROUP BY expression must contain at least one column that is not an outer reference.
  - sql: `SELECT a, COUNT_BIG(*) FROM t GROUP BY 1`
- **group_by_all** / sqlserver: SYNTAX_ERR: Incorrect syntax near 'ALL'.
  - sql: `SELECT a, COUNT_BIG(*) FROM t GROUP BY ALL`
- **group_by_all** / fabric: SYNTAX_ERR: Incorrect syntax near 'ALL'.
  - sql: `SELECT a, COUNT_BIG(*) FROM t GROUP BY ALL`
- **group_by_all** / postgres: EXEC_ERR: 42601: syntax error at end of input  POSITION: 39
  - sql: `SELECT a, COUNT(*) FROM t GROUP BY ALL`
- **list_literal** / sqlserver: EXEC_ERR: Invalid column name '1, 2, 3'.
  - sql: `SELECT [1, 2, 3] AS l`
- **list_literal** / fabric: EXEC_ERR: Invalid column name '1, 2, 3'.
  - sql: `SELECT [1, 2, 3] AS l`
- **struct_literal** / sqlserver: SYNTAX_ERR: Incorrect syntax near 'a'.
  - sql: `SELECT {'a': 1} AS s`
- **struct_literal** / fabric: SYNTAX_ERR: Incorrect syntax near 'a'.
  - sql: `SELECT {'a': 1} AS s`
- **struct_literal** / postgres: EXEC_ERR: 42601: syntax error at or near "{"  POSITION: 8
  - sql: `SELECT {'a': 1} AS s`
- **unnest** / sqlserver: EXEC_ERR: 'UNNEST' is not a recognized built-in function name.
  - sql: `SELECT UNNEST([1, 2, 3]) AS u`
- **unnest** / fabric: EXEC_ERR: 'UNNEST' is not a recognized built-in function name.
  - sql: `SELECT UNNEST([1, 2, 3]) AS u`
- **int_overflow** / sqlserver: BOTH_ERR: target: Arithmetic overflow error converting expression to data type int. | duckdb: Out of Range Error: Overflow in addition of INT32 (2147483647 + 1)!
  - sql: `SELECT CAST(2147483647 AS INTEGER) + 1 AS x`
- **int_overflow** / fabric: BOTH_ERR: target: Arithmetic overflow error converting expression to data type int. | duckdb: Out of Range Error: Overflow in addition of INT32 (2147483647 + 1)!
  - sql: `SELECT CAST(2147483647 AS INT) + 1 AS x`
- **int_overflow** / postgres: BOTH_ERR: target: 22003: integer out of range | duckdb: Out of Range Error: Overflow in addition of INT32 (2147483647 + 1)!
  - sql: `SELECT CAST(2147483647 AS INT) + 1 AS x`
- **try_cast** / sqlserver: MISMATCH: duckdb=[∅] target=[0] (sorted row 0)
  - sql: `SELECT TRY_CAST(s AS INTEGER) AS x FROM t`
- **try_cast** / fabric: MISMATCH: duckdb=[∅] target=[0] (sorted row 0)
  - sql: `SELECT TRY_CAST(s AS INT) AS x FROM t`
- **try_cast** / postgres: EXEC_ERR: 22P02: invalid input syntax for type integer: "abc"
  - sql: `SELECT CAST(s AS INT) AS x FROM t`
- **str_eq_profile** / sqlserver: MISMATCH: row count duckdb=1 target=4
  - sql: `SELECT * FROM t WHERE s = 'abc'`
- **str_eq_profile** / fabric: MISMATCH: row count duckdb=1 target=4
  - sql: `SELECT * FROM t WHERE s = 'abc'`
- **str_in_profile** / sqlserver: MISMATCH: row count duckdb=1 target=4
  - sql: `SELECT * FROM t WHERE s IN ('abc', 'x')`
- **str_in_profile** / fabric: MISMATCH: row count duckdb=1 target=4
  - sql: `SELECT * FROM t WHERE s IN ('abc', 'x')`
- **avg_int** / sqlserver: MISMATCH: duckdb=[4.571428571] target=[4] (sorted row 0)
  - sql: `SELECT AVG(a) AS x FROM t`
- **avg_int** / fabric: MISMATCH: duckdb=[4.571428571] target=[4] (sorted row 0)
  - sql: `SELECT AVG(a) AS x FROM t`
- **avg_decimal** / sqlserver: MISMATCH: duckdb=[4.571428571] target=[4.571428] (sorted row 0)
  - sql: `SELECT AVG(CAST(a AS NUMERIC(10, 2))) AS x FROM t`
- **avg_decimal** / fabric: MISMATCH: duckdb=[4.571428571] target=[4.571428] (sorted row 0)
  - sql: `SELECT AVG(CAST(a AS DECIMAL(10, 2))) AS x FROM t`
- **left_join_using** / sqlserver: SYNTAX_ERR: "a" is not a recognized table hints option.
  - sql: `SELECT * FROM t LEFT JOIN u USING (a)`
- **left_join_using** / fabric: SYNTAX_ERR: "a" is not a recognized table hints option.
  - sql: `SELECT * FROM t LEFT JOIN u USING (a)`
- **left_join_using** / postgres: MISMATCH: duckdb=[1|7|2|abc|2024-01-15|2024-01-15 10:30:45|2024-01-31|2024-02-01|1|100] target=[1|4|∅| abc|2023-12-31|2023-12-31 12:00:00|2024-01-01|2024-01-01|4|∅] (sorted row 0)
  - sql: `SELECT * FROM t LEFT JOIN u USING (a)`
- **natural_join** / sqlserver: SYNTAX_ERR: Incorrect syntax near 'u'.
  - sql: `SELECT * FROM t NATURAL JOIN u`
- **natural_join** / fabric: SYNTAX_ERR: Incorrect syntax near 'u'.
  - sql: `SELECT * FROM t NATURAL JOIN u`
- **select_distinct_on** / postgres: MISMATCH: duckdb=[7|2] target=[7|9] (sorted row 4)
  - sql: `SELECT DISTINCT ON (a) a, b FROM t`
- **sample_clause** / sqlserver: SYNTAX_ERR: Incorrect syntax near 'SAMPLE'.
  - sql: `SELECT * FROM t USING SAMPLE SYSTEM (50 PERCENT)`
- **sample_clause** / fabric: SYNTAX_ERR: Incorrect syntax near 'SAMPLE'.
  - sql: `SELECT * FROM t USING SAMPLE SYSTEM (50 PERCENT)`
- **sample_clause** / postgres: EXEC_ERR: 42601: syntax error at or near "USING"  POSITION: 17
  - sql: `SELECT * FROM t USING SAMPLE SYSTEM (50 PERCENT)`
- **abs_round** / postgres: EXEC_ERR: 42883: function round(double precision, integer) does not exist  POSITION: 25
  - sql: `SELECT ABS(a - 5) AS x, ROUND(CAST(CAST(a AS DECIMAL(10, 2)) AS DOUBLE PRECISION) / NULLIF(3, 0), 1) AS r FROM t`
- **replace_fn** / sqlserver: MISMATCH: duckdb=[ABC] target=[héllo] (sorted row 2)
  - sql: `SELECT REPLACE(s, 'a', 'z') AS x FROM t`
- **replace_fn** / fabric: MISMATCH: duckdb=[ABC] target=[héllo] (sorted row 2)
  - sql: `SELECT REPLACE(s, 'a', 'z') AS x FROM t`
- **max_by_cast_int** / sqlserver: BOTH_ERR: target: Conversion failed when converting the nvarchar value 'abc' to data type int. | duckdb: Conversion Error: Could not convert string 'abc' to INT32 when casting from source column s  LINE 1: SELECT CAST(s AS INTEGER) AS x FROM t WHERE s = 'abc'                ^
  - sql: `SELECT CAST(s AS INTEGER) AS x FROM t WHERE s = 'abc'`
- **max_by_cast_int** / fabric: BOTH_ERR: target: Conversion failed when converting the nvarchar value 'abc' to data type int. | duckdb: Conversion Error: Could not convert string 'abc' to INT32 when casting from source column s  LINE 1: SELECT CAST(s AS INTEGER) AS x FROM t WHERE s = 'abc'                ^
  - sql: `SELECT CAST(s AS INT) AS x FROM t WHERE s = 'abc'`
- **max_by_cast_int** / postgres: BOTH_ERR: target: 22P02: invalid input syntax for type integer: "abc" | duckdb: Conversion Error: Could not convert string 'abc' to INT32 when casting from source column s  LINE 1: SELECT CAST(s AS INTEGER) AS x FROM t WHERE s = 'abc'                ^
  - sql: `SELECT CAST(s AS INT) AS x FROM t WHERE s = 'abc'`
