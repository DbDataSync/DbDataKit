SELECT customers.region,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       index(MEASURE(orders.net_revenue), along := 'date.fiscal_month', base := '2025-09') AS growth
FROM sales_catalog
WHERE date.fiscal_year = 'FY26'
GROUP BY ALL
LIMIT 100
