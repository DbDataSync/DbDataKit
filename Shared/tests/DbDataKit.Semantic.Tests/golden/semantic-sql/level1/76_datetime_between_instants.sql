SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-07-01T12:00:00Z' AND '2026-07-01T18:00:00Z'
LIMIT 100
