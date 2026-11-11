SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       MEASURE(orders.order_count)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY CUBE (customers.region, products.product)
LIMIT 100
