SELECT customers.region,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'customers.region', base := 'West') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
