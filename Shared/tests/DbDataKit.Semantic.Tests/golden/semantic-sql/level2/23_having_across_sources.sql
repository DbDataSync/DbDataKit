SELECT customers.region,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
HAVING MEASURE(budget.budget_amount) > 550
LIMIT 100
