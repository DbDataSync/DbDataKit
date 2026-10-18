-- A Pareto classification: products ranked by revenue, each with the share of all revenue up to and including it; A is the products that make the first 80 percent, B the next 15, C the rest.
WITH revenue AS (
  SELECT product_id, sum(revenue_usd) AS revenue, sum(order_qty) AS units FROM marts.fct_sales_lines GROUP BY product_id
),
ranked AS (
  SELECT product_id, revenue, units,
         row_number() OVER (ORDER BY revenue DESC, product_id) AS revenue_rank,
         sum(revenue) OVER (ORDER BY revenue DESC, product_id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) / sum(revenue) OVER () AS cumulative_share
  FROM revenue
)
SELECT product_id,
       revenue_rank,
       CAST(revenue AS DECIMAL(19, 2)) AS revenue,
       CAST(units AS BIGINT) AS units,
       CAST(cumulative_share AS DECIMAL(9, 4)) AS cumulative_share,
       CASE WHEN cumulative_share <= 0.80 THEN 'A' WHEN cumulative_share <= 0.95 THEN 'B' ELSE 'C' END AS abc_class
FROM ranked
