SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       running_total(MEASURE(orders.net_revenue), along := 'date.fiscal_month') AS rev_cumulative
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
