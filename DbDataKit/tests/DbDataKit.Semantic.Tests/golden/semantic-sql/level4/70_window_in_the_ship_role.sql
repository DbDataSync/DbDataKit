SELECT ship_date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'ship_date.fiscal_month', start := -1, "end" := 'current') AS r2
FROM sales_catalog
GROUP BY ALL
LIMIT 100
