SELECT customers.region,
       products.product,
       MEASURE(orders.avg_order_value)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
