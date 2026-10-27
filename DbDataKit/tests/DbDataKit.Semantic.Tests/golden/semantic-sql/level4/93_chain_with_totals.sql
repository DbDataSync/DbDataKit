SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['products.product']) AS share_of_region,
       running_total(share_of_region, along := 'products.product') AS rev_cum
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ROLLUP (customers.region, products.product)
LIMIT 100
