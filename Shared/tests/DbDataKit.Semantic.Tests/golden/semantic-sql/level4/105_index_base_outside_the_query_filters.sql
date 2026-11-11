SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'date.fiscal_month', base := '2025-07') AS growth
FROM sales_catalog
WHERE date.fiscal_month IN ('2026-07', '2026-08', '2026-09')
GROUP BY ALL
LIMIT 100
