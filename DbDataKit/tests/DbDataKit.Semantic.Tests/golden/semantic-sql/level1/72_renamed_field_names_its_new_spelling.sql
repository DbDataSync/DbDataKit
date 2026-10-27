SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog(x_totals := {'rollup': ['customers.region']})
GROUP BY ALL
LIMIT 100
