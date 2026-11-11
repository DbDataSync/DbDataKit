SELECT date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_year = 'FY26'
GROUP BY ALL
LIMIT 100
