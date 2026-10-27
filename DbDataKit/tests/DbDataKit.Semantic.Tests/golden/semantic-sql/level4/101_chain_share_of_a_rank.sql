SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       rank(MEASURE(orders.net_revenue), within := ['customers.region']) AS rev_rank,
       pct_of_total(rev_rank, over := ['products.product']) AS rank_share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
