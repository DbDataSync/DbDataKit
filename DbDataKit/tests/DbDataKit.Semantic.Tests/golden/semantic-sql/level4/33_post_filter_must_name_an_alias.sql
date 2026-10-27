SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['products.product']) AS share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
QUALIFY revenue_rank > 100
LIMIT 100
