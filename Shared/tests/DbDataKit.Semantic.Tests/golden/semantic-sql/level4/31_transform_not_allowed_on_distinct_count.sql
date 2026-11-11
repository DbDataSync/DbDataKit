SELECT customers.region,
       products.product,
       pct_of_total(MEASURE(orders.order_count), over := ['products.product']) AS share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
