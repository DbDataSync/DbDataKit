-- The three products with the most revenue in every territory. LATERAL lets the subquery read the territory it is joined to, which is SQL Server's CROSS APPLY.
SELECT t.territory_id, t.territory_name, top.product_id, top.revenue
FROM marts.dim_territory t,
LATERAL (
  SELECT f.product_id, CAST(sum(f.revenue_usd) AS DECIMAL(19, 2)) AS revenue
  FROM marts.fct_sales_lines f
  WHERE f.territory_id = t.territory_id
  GROUP BY f.product_id
  ORDER BY sum(f.revenue_usd) DESC, f.product_id
  LIMIT 3
) top
