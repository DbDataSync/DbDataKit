SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['customers.region', 'products.product']) AS share,
       rank(MEASURE(orders.net_revenue), ties := 'break', keep := 2) AS rev_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
