SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       rank(MEASURE(orders.net_revenue), within := ['customers.region']) AS rev_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
QUALIFY rev_rank <= 1
LIMIT 100
