SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND (customers.region = 'East' OR products.product = 'A')
GROUP BY ALL
LIMIT 100
