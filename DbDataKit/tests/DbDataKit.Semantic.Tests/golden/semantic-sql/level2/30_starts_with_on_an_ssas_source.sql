SELECT customers.region,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND starts_with(customers.region, 'E')
GROUP BY ALL
LIMIT 100
