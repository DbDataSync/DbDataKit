SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), output := 'value', period := {'unit': 'fiscal_year', 'offset': -1}) AS rev_prior
FROM sales_catalog
WHERE date.fiscal_month >= '2026-08'
GROUP BY ALL
LIMIT 100
