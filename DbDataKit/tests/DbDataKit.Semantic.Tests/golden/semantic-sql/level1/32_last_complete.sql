SELECT date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE last_complete(date.fiscal_month, 2)
GROUP BY ALL
LIMIT 100
