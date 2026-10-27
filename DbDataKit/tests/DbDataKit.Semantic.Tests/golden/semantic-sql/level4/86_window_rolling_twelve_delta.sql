SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := -11, "end" := 'current', output := 'delta') AS vs_r12
FROM sales_catalog
GROUP BY ALL
LIMIT 100
