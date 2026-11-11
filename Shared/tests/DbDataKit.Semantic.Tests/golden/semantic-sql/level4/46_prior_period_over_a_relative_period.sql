SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), period := {'unit': 'fiscal_year', 'offset': -1}, output := 'value') AS rev_prior
FROM sales_catalog
WHERE last_complete(date.fiscal_month, 2)
GROUP BY ALL
LIMIT 100
