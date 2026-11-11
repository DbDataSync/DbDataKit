SELECT customers.region,
       MEASURE(orders.net_revenue) FILTER (WHERE orders.status = 'Completed') AS completed_revenue,
       MEASURE(orders.net_revenue) FILTER (WHERE orders.status = 'Cancelled') AS completed_revenue
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
