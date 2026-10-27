SELECT date.fiscal_month,
       MEASURE(budget.budget_amount),
       "window"(MEASURE(budget.budget_amount), along := 'date.fiscal_month', start := -2, "end" := 'current') AS r3
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
