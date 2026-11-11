SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       pct_of_total(MEASURE(orders.net_revenue), over := ['date.fiscal_month']) AS share,
       running_total(share, along := 'date.fiscal_month') AS cum_share,
       rank(cum_share, within := []) AS cum_rank
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
