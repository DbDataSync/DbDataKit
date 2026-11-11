SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog(x_note := 'for the planner')
GROUP BY ALL
LIMIT 100
