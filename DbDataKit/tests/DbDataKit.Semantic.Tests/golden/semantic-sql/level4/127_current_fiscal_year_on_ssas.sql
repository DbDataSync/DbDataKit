SELECT date.fiscal_year,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE current(date.fiscal_year)
GROUP BY ALL
LIMIT 100
