SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := -2) AS x
FROM sales_catalog
GROUP BY ALL
LIMIT 100
