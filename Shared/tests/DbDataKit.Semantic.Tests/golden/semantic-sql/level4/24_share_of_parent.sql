SELECT date.fiscal_quarter,
       date.fiscal_month,
       MEASURE(orders.net_revenue),
       share_of_parent(MEASURE(orders.net_revenue), attribute := 'date.fiscal_month', parent := 'date.fiscal_quarter') AS month_share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
