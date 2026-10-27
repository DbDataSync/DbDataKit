SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE current(customers.region)
LIMIT 100
