SELECT orders.order_count,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE customers.region > 'M'
GROUP BY ALL
LIMIT 100
