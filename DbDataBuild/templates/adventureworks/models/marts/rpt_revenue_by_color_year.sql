-- Revenue by colour group with one column per year. PIVOT turns the values of `year` into columns (a fixed list, so the columns are known when the model is defined); the outer
-- query names them.
SELECT color_group, "2022" AS revenue_2022, "2023" AS revenue_2023, "2024" AS revenue_2024
FROM (
  PIVOT (
    SELECT d.year AS year, p.color_group AS color_group, f.revenue_usd AS revenue_usd
    FROM marts.fct_sales_lines f
    JOIN marts.dim_date d ON d.date_key = f.date_key
    JOIN marts.dim_product p ON p.product_id = f.product_id
  )
  ON year IN (2022, 2023, 2024)
  USING sum(revenue_usd)
  GROUP BY color_group
)
