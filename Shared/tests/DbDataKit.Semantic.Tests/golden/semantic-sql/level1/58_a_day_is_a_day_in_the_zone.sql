SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-08-01' AND '2026-08-01'
LIMIT 100
