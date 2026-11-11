SELECT customers.region,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'customers.region', start := -2, "end" := 'current') AS ytd
FROM sales_catalog
GROUP BY ALL
LIMIT 100
