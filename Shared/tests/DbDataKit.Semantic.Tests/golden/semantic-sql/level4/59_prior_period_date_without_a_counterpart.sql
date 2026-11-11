SELECT date.fiscal_month,
       MEASURE(orders.net_revenue),
       prior_period(MEASURE(orders.net_revenue), period := {'unit': 'fiscal_year', 'offset': -1}, output := 'value') AS rev_prior
FROM sales_catalog
WHERE date.calendar_date = '2026-09-20'
GROUP BY ALL
LIMIT 100
