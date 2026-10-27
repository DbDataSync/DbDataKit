SELECT date.calendar_date,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE last_complete(date.calendar_date, 1)
GROUP BY ALL
LIMIT 100
