SELECT customers.region,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE last_complete(date.fiscal_month, 2)
GROUP BY ALL
LIMIT 100
