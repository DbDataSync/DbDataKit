SELECT customers.region,
       MEASURE(orders.net_revenue) FILTER (WHERE orders.status = 'Completed') AS completed
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
HAVING completed >= 400
LIMIT 100
