SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
HAVING MEASURE(orders.net_revenue) BETWEEN 150 AND 300
LIMIT 100
