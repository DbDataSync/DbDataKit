SELECT customers.region,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       "window"(MEASURE(orders.net_revenue), along := 'date.fiscal_month', start := {'period_start': 'date.fiscal_year'}, "end" := 'current') AS ytd
FROM sales_catalog
WHERE date.fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
