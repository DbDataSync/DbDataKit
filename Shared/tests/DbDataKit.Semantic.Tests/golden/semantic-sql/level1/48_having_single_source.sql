SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
HAVING MEASURE(orders.net_revenue) > 450
LIMIT 100
