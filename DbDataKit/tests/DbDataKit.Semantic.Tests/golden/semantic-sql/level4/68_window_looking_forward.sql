SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := 0, "end" := 1) AS next2
FROM sales_catalog
WHERE date.fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
