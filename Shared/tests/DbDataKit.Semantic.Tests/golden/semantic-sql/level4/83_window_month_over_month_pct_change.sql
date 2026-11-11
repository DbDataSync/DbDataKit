SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', "offset" := -1, output := 'pct_change') AS growth
FROM sales_catalog
GROUP BY ALL
LIMIT 100
