SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
ORDER BY MEASURE(orders.net_revenue) DESC
LIMIT 3
