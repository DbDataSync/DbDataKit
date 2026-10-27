SELECT customers.region,
       products.product,
       MEASURE(orders.net_revenue),
       rank(MEASURE(orders.net_revenue), within := ['customers.region'], keep := 1) AS rev_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
