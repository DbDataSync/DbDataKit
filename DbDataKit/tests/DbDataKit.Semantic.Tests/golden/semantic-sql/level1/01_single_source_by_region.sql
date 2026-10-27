SELECT customers.region,
       MEASURE(orders.net_revenue),
       MEASURE(orders.order_count)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
ORDER BY MEASURE(orders.net_revenue) DESC
LIMIT 100
