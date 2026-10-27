SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.calendar_date = 'July 1 2026'
GROUP BY ALL
LIMIT 100
