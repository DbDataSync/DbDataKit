SELECT customers.region,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
  AND (MEASURE(orders.net_revenue) > 450 OR customers.region = 'East')
GROUP BY ALL
LIMIT 100
