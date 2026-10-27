SELECT user_territories.territory,
       MEASURE(orders.net_revenue),
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
