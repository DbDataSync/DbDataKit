SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'delta', period := {'unit': 'fiscal_year', 'offset': -1}) AS rev_delta,
       running_total(rev_delta, along := 'date.fiscal_month') AS delta_cum
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
