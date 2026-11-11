SELECT date.calendar_date,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.calendar_date < '2026-01-01'
GROUP BY ALL
LIMIT 100
