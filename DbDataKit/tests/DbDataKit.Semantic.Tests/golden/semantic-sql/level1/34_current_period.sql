SELECT date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE current(date.fiscal_quarter)
GROUP BY ALL
LIMIT 100
