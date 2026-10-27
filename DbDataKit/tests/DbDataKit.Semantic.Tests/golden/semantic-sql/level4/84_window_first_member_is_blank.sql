SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', "offset" := -1) AS prev,
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', "offset" := -1, output := 'delta') AS change,
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', "offset" := -1, output := 'pct_change') AS growth
FROM sales_catalog
WHERE date.fiscal_month = '2025-07'
GROUP BY ALL
LIMIT 100
