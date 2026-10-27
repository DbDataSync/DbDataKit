SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['date.fiscal_month']) AS share,
       index(share, along := 'date.fiscal_month', base := '2026-07') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
