SELECT date.fiscal_month,
       MEASURE(orders.avg_order_value),
       "window"(MEASURE(orders.avg_order_value), along := 'date.fiscal_month', "offset" := -1) AS prev
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
