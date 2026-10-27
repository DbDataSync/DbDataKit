SELECT ship_date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'ship_date.fiscal_month', base := '2025-08') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
