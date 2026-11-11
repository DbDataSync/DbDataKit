SELECT orders.ordered_at_week,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-06-29' AND '2026-07-05'
GROUP BY ALL
LIMIT 100
