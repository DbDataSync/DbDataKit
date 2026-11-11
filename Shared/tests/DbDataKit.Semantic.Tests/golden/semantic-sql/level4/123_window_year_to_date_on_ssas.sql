SELECT date.fiscal_month,
       MEASURE(budget.budget_amount),
       "window"(MEASURE(budget.budget_amount), along := 'date.fiscal_month', start := {'period_start': 'date.fiscal_year'}, "end" := 'current') AS ytd
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
