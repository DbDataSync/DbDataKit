SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE operator(date.fiscal_month, 'last_complete')
LIMIT 100
