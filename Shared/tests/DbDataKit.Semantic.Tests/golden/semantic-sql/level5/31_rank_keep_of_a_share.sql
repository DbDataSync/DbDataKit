SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['products.product']) AS share,
       rank(share, within := ['customers.region'], keep := 1) AS share_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
