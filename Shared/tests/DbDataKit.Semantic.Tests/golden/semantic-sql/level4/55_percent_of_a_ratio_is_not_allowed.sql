SELECT customers.region,
       MEASURE(orders.avg_order_value),
       pct_of_total(MEASURE(orders.avg_order_value), over := ['customers.region']) AS s
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
