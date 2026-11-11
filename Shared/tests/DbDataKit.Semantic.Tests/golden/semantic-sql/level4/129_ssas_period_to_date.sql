SELECT customers.region,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE period_to_date(date.fiscal_quarter)
GROUP BY ALL
LIMIT 100
