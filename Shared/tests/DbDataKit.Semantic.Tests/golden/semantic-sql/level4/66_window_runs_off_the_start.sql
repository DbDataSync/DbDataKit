SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := -2, "end" := 'current') AS r3
FROM sales_catalog
WHERE date.fiscal_month IN ('2025-07', '2025-08')
GROUP BY ALL
LIMIT 100
