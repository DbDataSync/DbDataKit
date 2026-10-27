SELECT date.fiscal_quarter,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE period_to_date(date.fiscal_quarter)
GROUP BY ALL
LIMIT 100
