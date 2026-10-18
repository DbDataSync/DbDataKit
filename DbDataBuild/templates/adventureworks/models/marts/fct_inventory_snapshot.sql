-- Stock at the end of every month next to what was sold that month and the average over the last three months, so months of supply can be read straight off.
WITH sold AS (
  SELECT f.product_id, d.year_month, sum(f.order_qty) AS units_sold
  FROM marts.fct_sales_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  GROUP BY f.product_id, d.year_month
),
monthly AS (
  SELECT s.snapshot_date, year(s.snapshot_date) * 100 + month(s.snapshot_date) AS year_month, s.product_id, s.quantity
  FROM staging.inventory_snapshots s
),
joined AS (
  SELECT m.snapshot_date, m.year_month, m.product_id, m.quantity, CAST(coalesce(x.units_sold, 0) AS BIGINT) AS units_sold
  FROM monthly m
  LEFT JOIN sold x ON x.product_id = m.product_id AND x.year_month = m.year_month
)
SELECT snapshot_date,
       year_month,
       product_id,
       quantity,
       units_sold,
       CAST(avg(CAST(units_sold AS DOUBLE)) OVER (PARTITION BY product_id ORDER BY year_month ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS DECIMAL(12, 2)) AS units_sold_3m_avg,
       CAST(quantity / nullif(avg(CAST(units_sold AS DOUBLE)) OVER (PARTITION BY product_id ORDER BY year_month ROWS BETWEEN 2 PRECEDING AND CURRENT ROW), 0) AS DECIMAL(12, 2)) AS months_of_supply
FROM joined
