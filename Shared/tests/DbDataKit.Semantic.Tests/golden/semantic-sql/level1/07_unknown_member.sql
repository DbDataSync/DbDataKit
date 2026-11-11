SELECT customers.region,
       MEASURE(orders.revenue)
FROM sales_catalog
GROUP BY ALL
LIMIT 100
