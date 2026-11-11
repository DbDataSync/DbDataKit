SELECT customers.region,
       per_entity(MEASURE(orders.net_revenue), entity := 'customers.customer', agg := 'avg') AS avg_rev_per_customer
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ROLLUP (customers.region)
LIMIT 100
