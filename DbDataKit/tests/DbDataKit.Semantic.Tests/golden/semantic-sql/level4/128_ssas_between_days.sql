SELECT customers.region,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2026-07-01' AND '2026-08-31'
GROUP BY ALL
LIMIT 100
