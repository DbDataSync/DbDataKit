SELECT date.fiscal_month,
       MEASURE(orders.shipped_revenue),
       prior_period(MEASURE(orders.shipped_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS shipped_prior
FROM sales_catalog
WHERE date.fiscal_quarter = 'FY27-Q1'
GROUP BY ALL
LIMIT 100
