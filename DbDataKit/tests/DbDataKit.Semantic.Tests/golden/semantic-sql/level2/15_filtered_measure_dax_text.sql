SELECT customers.region,
       MEASURE(budget.budget_amount) FILTER (WHERE customers.region = 'West') AS west_budget
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
