SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog(segments := ['orders.completed'])
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
