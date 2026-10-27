SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE customers.region = 'Wset'
GROUP BY ALL
LIMIT 100
