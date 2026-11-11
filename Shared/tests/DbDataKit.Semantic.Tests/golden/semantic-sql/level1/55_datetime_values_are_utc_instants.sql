SELECT orders.ordered_at,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at >= '2026-07-01T00:00:00Z'
  AND orders.ordered_at < '2026-07-02T00:00:00Z'
GROUP BY ALL
LIMIT 100
