SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at >= '2026-07-01'
  AND orders.ordered_at <= '2026-07-01'
LIMIT 100
