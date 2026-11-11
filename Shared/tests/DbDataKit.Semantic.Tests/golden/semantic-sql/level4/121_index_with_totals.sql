SELECT customers.region,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'date.fiscal_month', base := '2026-07') AS growth
FROM sales_catalog
WHERE date.fiscal_month IN ('2026-07', '2026-08')
GROUP BY ROLLUP (customers.region, date.fiscal_month)
LIMIT 100
