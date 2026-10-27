SELECT date.fiscal_year,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE last_complete(date.fiscal_year, 1)
GROUP BY ALL
LIMIT 100
