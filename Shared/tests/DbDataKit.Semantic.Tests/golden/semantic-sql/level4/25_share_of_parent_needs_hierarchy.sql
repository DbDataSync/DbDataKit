SELECT customers.region,
       products.product,
       share_of_parent(MEASURE(orders.net_revenue), attribute := 'products.product', parent := 'customers.region') AS s
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
