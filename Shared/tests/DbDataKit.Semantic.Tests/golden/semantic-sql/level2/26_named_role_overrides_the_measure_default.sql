SELECT order_date.fiscal_month,
       MEASURE(orders.shipped_revenue)
FROM sales_catalog
WHERE order_date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
