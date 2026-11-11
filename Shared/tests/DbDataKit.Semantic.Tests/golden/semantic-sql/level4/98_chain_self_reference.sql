SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       running_total(cum_share, along := 'date.fiscal_month') AS cum_share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
