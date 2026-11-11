SELECT orders.ordered_at_hour,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-07-01' AND '2026-07-01'
GROUP BY ALL
LIMIT 100
