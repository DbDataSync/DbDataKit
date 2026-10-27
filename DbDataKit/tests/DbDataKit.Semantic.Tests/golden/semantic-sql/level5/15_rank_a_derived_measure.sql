SELECT customers.region,
       MEASURE(orders.avg_order_value),
       rank(MEASURE(orders.avg_order_value)) AS aov_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
