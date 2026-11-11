SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       running_total(share, along := 'date.fiscal_month') AS cum_share,
       pct_of_total(MEASURE(orders.net_revenue), over := ['date.fiscal_month']) AS share
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
