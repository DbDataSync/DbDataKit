SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE current(orders.ordered_at_hour)
LIMIT 100
