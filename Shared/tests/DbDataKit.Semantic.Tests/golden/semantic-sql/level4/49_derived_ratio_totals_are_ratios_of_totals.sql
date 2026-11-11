SELECT customers.region,
       MEASURE(orders.avg_order_value)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ROLLUP (customers.region)
LIMIT 100
