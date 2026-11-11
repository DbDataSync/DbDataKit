SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE customers.region BETWEEN '2026-07-01' AND '2026-09-01'
GROUP BY ALL
LIMIT 100
