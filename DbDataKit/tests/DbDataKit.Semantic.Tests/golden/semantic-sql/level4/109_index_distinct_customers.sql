SELECT date.fiscal_month,
       MEASURE(orders.customer_count),
       index(MEASURE(orders.customer_count), along := 'date.fiscal_month', base := '2026-07') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
