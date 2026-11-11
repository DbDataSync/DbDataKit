SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ROLLUP (customers.region, products.product)
LIMIT 4
