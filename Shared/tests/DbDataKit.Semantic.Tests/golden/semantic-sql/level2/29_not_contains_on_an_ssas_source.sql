SELECT customers.region,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND NOT contains(customers.region, 'es')
GROUP BY ALL
LIMIT 100
