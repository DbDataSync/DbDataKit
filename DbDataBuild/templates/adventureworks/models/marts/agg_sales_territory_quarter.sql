-- Revenue per territory and quarter, the running total within its year, and the same quarter of the year before with the growth against it.
WITH quarterly AS (
  SELECT f.territory_id, d.year, d.quarter,
         sum(f.revenue_usd) AS revenue,
         sum(f.margin_usd) AS margin,
         count(DISTINCT f.sales_order_id) AS orders
  FROM marts.fct_sales_lines f
  JOIN marts.dim_date d ON d.date_key = f.date_key
  GROUP BY f.territory_id, d.year, d.quarter
)
SELECT q.territory_id,
       q.year,
       q.quarter,
       CAST(q.revenue AS DECIMAL(19, 2)) AS revenue,
       CAST(q.margin AS DECIMAL(19, 2)) AS margin,
       q.orders,
       CAST(sum(q.revenue) OVER (PARTITION BY q.territory_id, q.year ORDER BY q.quarter ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS DECIMAL(19, 2)) AS revenue_year_to_date,
       CAST(py.revenue AS DECIMAL(19, 2)) AS revenue_prior_year,
       CAST(round((q.revenue - py.revenue) / nullif(py.revenue, 0) * 100, 1) AS DECIMAL(9, 1)) AS growth_percent
FROM quarterly q
LEFT JOIN quarterly py ON py.territory_id = q.territory_id AND py.year = q.year - 1 AND py.quarter = q.quarter
