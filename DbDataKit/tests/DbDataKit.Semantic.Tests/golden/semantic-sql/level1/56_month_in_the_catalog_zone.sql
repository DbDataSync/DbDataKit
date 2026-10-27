SELECT orders.ordered_at_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE orders.ordered_at BETWEEN '2026-06-01' AND '2026-09-30'
GROUP BY ALL
LIMIT 100
