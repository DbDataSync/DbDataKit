SELECT customers.region,
       MEASURE(orders.net_revenue) FILTER (WHERE orders.status = 'Completed') AS completed_revenue,
       MEASURE(budget.budget_amount)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
