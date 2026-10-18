-- UNPIVOT turns the columns `revenue` and `sales_quota` into rows: one row per sales person, quarter and measure. A row whose amount is NULL would be dropped, as DuckDB does (neither column can be NULL here).
SELECT * FROM (
  UNPIVOT (SELECT sales_person_id, year, quarter, revenue, sales_quota FROM marts.rpt_sales_person_quota)
  ON revenue, sales_quota
  INTO NAME measure VALUE amount
)
