SELECT ship_date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'ship_date.fiscal_month', start := {'period_start': 'ship_date.fiscal_year'}, "end" := 'current') AS ytd
FROM sales_catalog
WHERE ship_date.fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
