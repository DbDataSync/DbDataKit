SELECT date.fiscal_year,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'date.fiscal_month', base := '2025-07') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
