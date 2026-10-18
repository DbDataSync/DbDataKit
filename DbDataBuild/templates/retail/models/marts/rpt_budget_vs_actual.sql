-- Budget against actual net sales for each shop, month and category: the allocated budget, what the shop sold, and the difference.
WITH actual AS (
  SELECT d.year_month AS budget_month, l.store_id, p.category_code, sum(l.net_amount) AS net_amount
  FROM marts.fct_sales_lines l
  JOIN marts.dim_date d ON d.date_key = l.date_key
  JOIN marts.dim_product p ON p.product_id = l.product_id
  GROUP BY d.year_month, l.store_id, p.category_code
)
SELECT a.budget_month,
       a.region,
       a.category_code,
       a.store_id,
       a.allocated_budget,
       CAST(coalesce(x.net_amount, 0) AS DECIMAL(18, 2)) AS net_sales,
       CAST(coalesce(x.net_amount, 0) - a.allocated_budget AS DECIMAL(18, 2)) AS variance
FROM marts.fct_budget_allocation a
LEFT JOIN actual x ON x.budget_month = a.budget_month AND x.store_id = a.store_id AND x.category_code = a.category_code
