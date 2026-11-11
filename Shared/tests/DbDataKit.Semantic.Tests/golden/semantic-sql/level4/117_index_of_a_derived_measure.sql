SELECT date.fiscal_month,
       MEASURE(orders.avg_order_value),
       index(MEASURE(orders.avg_order_value), along := 'date.fiscal_month', base := '2026-07') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
