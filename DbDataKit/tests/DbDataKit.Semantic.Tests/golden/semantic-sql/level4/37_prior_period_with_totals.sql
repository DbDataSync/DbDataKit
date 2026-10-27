SELECT customers.region,
       date.fiscal_quarter,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'pct_change', period := {'unit': 'fiscal_year', 'offset': -1}) AS rev_yoy
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY date.fiscal_quarter, ROLLUP (customers.region)
LIMIT 100
