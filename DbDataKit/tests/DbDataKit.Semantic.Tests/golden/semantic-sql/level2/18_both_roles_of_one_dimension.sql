SELECT date.fiscal_month,
       ship_date.fiscal_month,
       MEASURE(orders.net_revenue)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
