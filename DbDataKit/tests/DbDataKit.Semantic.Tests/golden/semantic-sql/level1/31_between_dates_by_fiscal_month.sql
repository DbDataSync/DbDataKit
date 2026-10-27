SELECT date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.calendar_date BETWEEN '2026-07-01' AND '2026-08-31'
GROUP BY ALL
LIMIT 100
