SELECT date.fiscal_month,
       date.calendar_date,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := -1, "end" := 'current') AS x
FROM sales_catalog
GROUP BY ALL
LIMIT 100
