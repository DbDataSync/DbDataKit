SELECT customers.region,
       date.fiscal_quarter,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2025-07-01' AND '2026-12-31'
GROUP BY ALL
LIMIT 100
