SELECT date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_month BETWEEN '2026-07' AND '2026-08'
GROUP BY ALL
LIMIT 100
