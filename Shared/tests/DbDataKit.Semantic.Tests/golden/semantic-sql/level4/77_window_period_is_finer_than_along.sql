SELECT date.fiscal_year,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_year', start := {'period_start': 'date.fiscal_month'}, "end" := 'current') AS x
FROM sales_catalog
GROUP BY ALL
LIMIT 100
