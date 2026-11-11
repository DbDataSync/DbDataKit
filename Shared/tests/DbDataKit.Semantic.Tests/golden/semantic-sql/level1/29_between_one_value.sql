SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE operator(date.calendar_date, 'between', '2026-09-01')
GROUP BY ALL
LIMIT 100
