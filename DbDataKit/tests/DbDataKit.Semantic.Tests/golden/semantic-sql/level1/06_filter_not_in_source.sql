SELECT customers.region,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND products.category = 'Laptops'
GROUP BY ALL
LIMIT 100
