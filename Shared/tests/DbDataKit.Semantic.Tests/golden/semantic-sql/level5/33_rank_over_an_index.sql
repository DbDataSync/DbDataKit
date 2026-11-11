SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'date.fiscal_month', base := '2026-07') AS growth,
       rank(growth, "order" := 'desc') AS growth_rank
FROM sales_catalog
WHERE date.fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
