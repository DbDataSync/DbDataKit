SELECT user_territories.territory,
       MEASURE(orders.order_count)
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
