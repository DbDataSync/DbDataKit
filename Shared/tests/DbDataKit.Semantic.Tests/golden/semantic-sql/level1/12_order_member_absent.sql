SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
GROUP BY ALL
ORDER BY budget.budget_amount DESC
LIMIT 100
