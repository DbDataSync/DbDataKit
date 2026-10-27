SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -2}) AS two_back
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
