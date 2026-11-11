SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       MEASURE(orders.shipped_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
