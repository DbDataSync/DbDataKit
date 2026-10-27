SELECT date.fiscal_month,
       MEASURE(orders.customer_count),
       "window"(MEASURE(orders.customer_count), along := 'date.fiscal_month', start := {'period_start': 'date.fiscal_year'}, "end" := 'current') AS customers_ytd
FROM sales_catalog
WHERE date.fiscal_year = 'FY27'
GROUP BY ALL
LIMIT 100
