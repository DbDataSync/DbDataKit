SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE operator(date.fiscal_month, 'current', count := 2)
LIMIT 100
