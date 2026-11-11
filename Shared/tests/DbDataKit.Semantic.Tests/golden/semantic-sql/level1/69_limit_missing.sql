SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
GROUP BY ALL
