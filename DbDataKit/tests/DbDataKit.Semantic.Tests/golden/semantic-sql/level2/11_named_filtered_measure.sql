SELECT customers.region,
       MEASURE(orders.completed_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
