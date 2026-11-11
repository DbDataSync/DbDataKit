SELECT date.fiscal_month,
       MEASURE(budget.budget_amount),
       index(MEASURE(budget.budget_amount), along := 'date.fiscal_month', base := '2025-07') AS ix
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
