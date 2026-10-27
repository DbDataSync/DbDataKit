SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2026-13-01' AND '2026-12-01'
LIMIT 100
