SELECT MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE period_to_date(date.fiscal_month)
LIMIT 100
